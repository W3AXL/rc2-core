using Serilog;
using SIPSorcery.Net;
using SIPSorcery.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using System.Net;
using NAudio;
using NAudio.Wave;
using NAudio.Utils;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using Concentus;
using System.Timers;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using WebSocketSharp;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace rc2_core
{
    /// <summary>
    /// Logging sink used to detect errors/exceptions within SipSorcery and restart the WebRTC peer connection
    /// </summary>
    public class SrtpFailureSink : ILogEventSink
    {
        private static int failCount = 0;
        private const int failThresh = 3;

        public event Action<string> OnFailure;
        
        public void Emit(LogEvent ev)
        {
            if (ev.MessageTemplate.Text.Contains("SRTP unprotect failed for audio"))
            {
                failCount++;
            }

            if (failCount > failThresh)
            {
                OnFailure?.Invoke("SRTP failures exceeded threshold");
            }
        }

        /// <summary>
        /// Reset the failure counters
        /// </summary>
        public void Reset()
        {
            failCount = 0;
        }
    }

    public partial class WebRTCPeer : WebSocketSharp.Server.WebSocketBehavior
    {
        // Objects for RX audio processing
        private AudioEncoder RxEncoder;
        
        // Objects for TX audio processing
        public AudioEncoder TxEncoder;

        // We make separate encoders for recording since some codecs can be time-variant
        public AudioEncoder RecRxEncoder;
        private AudioEncoder RecTxEncoder;

        // TX audio output samplerate
        private int txAudioSamplerate;

        // Watchdog timers for missing TX/RX samples
        private System.Timers.Timer rxSampleTimer = new System.Timers.Timer(500);
        private System.Timers.Timer txSampleTimer = new System.Timers.Timer(500);

        // Objects for TX/RX audio recording
        public bool Record = false;          // Whether or not recording to audio files is enabled
        public string RecPath = "";        // Folder to store recordings
        public string RecTsFmt = "yyyy-MM-dd_HHmmss";       // Timestamp format string
        public bool RecTxInProgress = false;   // Flag to indicate if a file is currently being recorded
        public bool RecRxInProgress = false;
        private float recRxGain = 1;
        private float recTxGain = 1;

        // Recording format (TODO: Make configurable)
        private WaveFormat recFormat;
        // Output wave file writers
        private WaveFileWriter recTxWriter;
        private WaveFileWriter recRxWriter;

        // Common WebRTC variables
        private RTCPeerConnection pc;
        public const string Codec = "G722";
        private AudioFormat audioFmt = AudioFormat.Empty;
        private MediaStreamTrack audioTrack;
        private uint syncSource;
        private SrtpFailureSink srtpFailureSink;

        // Flag whether our radio is RX only
        public bool RxOnly {get; set;} = false;

        // Event for when the WebRTC connection connects
        public event EventHandler OnWebRTCConnect;

        // Event for when the WebRTC connection closes
        public event EventHandler OnWebRTCClose;

        // Callback for receiving audio from the peer connection
        private Action<short[]> TxCallback;

        // Low pass filters for resampling
        private BiQuadFilter rxResamplingLowPassFilter;
        private BiQuadFilter txResamplingLowPassFilter;

        /// <summary>
        /// Callback for when the audio formats have been negotiated for the peer connection
        /// </summary>
        public Action<AudioFormat> RTCFormatCallback;

        /// <summary>
        /// Class to hold a WebRTC signaling message received over a websocket connection
        /// </summary>
        public class SignalingMessage
        {
            public enum MessageType
            {
                OFFER,
                ANSWER,
                CANDIDATE
            }
            public MessageType Type { get; set; }
            public string SDP { get; set; }
            public string Candidate { get; set; }
            public string SDPMid { get; set; }
            public ushort? SDPMLineIndex { get; set; }
        }

        public WebRTCPeer(Action<short[]> txCallback, int txSampleRate)
        {
            // Create RX encoders
            RxEncoder = new AudioEncoder();
            RecRxEncoder = new AudioEncoder();

            // Setup sample watchdogs (stopped for now, they get started when the first samples start flowing)
            txSampleTimer.Elapsed += missingTxSampleCallback;
            txSampleTimer.Enabled = false;
            rxSampleTimer.Elapsed += missingRxSampleCallback;
            rxSampleTimer.Enabled = false;

            // Create TX encoders if we aren't RX only
            if (!RxOnly)
            {
                TxEncoder = new AudioEncoder();
                RecTxEncoder = new AudioEncoder();
                // Bind tx audio callback
                TxCallback = txCallback;
                txAudioSamplerate = txSampleRate;
            }

            // Enable hook for SRTP failure monitoring
            srtpFailureSink = new SrtpFailureSink();
            srtpFailureSink.OnFailure += (reason) => _ = RestartPeerConnection(reason);
            Serilog.Core.Logger srtpLogger = new LoggerConfiguration().WriteTo.Sink(srtpFailureSink).CreateLogger();

            // Enable SipSorcery Logging
            Microsoft.Extensions.Logging.ILoggerFactory factory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(Serilog.Log.Logger);
                builder.AddSerilog(srtpLogger);
            });
            SIPSorcery.LogFactory.Set(factory);
        }

        protected override async void OnOpen()
        {
            await initPeerConnection();
        }

        private async Task initPeerConnection()
        {
            pc = await CreatePeerConnection();
            connectEventHandlers();
        }

        protected override async void OnMessage(MessageEventArgs e)
        {
            SignalingMessage? msg = JsonSerializer.Deserialize<SignalingMessage>(e.Data);

            if (msg == null)
                throw new InvalidDataException($"Unable to deserialize JSON: {e.Data}");

            switch (msg.Type)
            {
                case SignalingMessage.MessageType.OFFER:
                    await handleOffer(msg.SDP);
                    break;
                case SignalingMessage.MessageType.ANSWER:
                    pc.setRemoteDescription(new RTCSessionDescriptionInit 
                    { 
                        type = RTCSdpType.answer,
                        sdp = msg.SDP 
                    });
                    break;
                case SignalingMessage.MessageType.CANDIDATE:
                    pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = msg.Candidate,
                        sdpMid = msg.SDPMid,
                        sdpMLineIndex = msg.SDPMLineIndex ?? 0
                    });
                    break;
            }
        }

        private async Task handleOffer(string sdp)
        {
            // Set the remote description
            pc.setRemoteDescription(
                new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = sdp
                }
            );
            // Create an answer and send it
            RTCSessionDescriptionInit answer = pc.createAnswer();
            await pc.setLocalDescription(answer);
            Send(JsonSerializer.Serialize(
                new
                {
                    type = "answer",
                    sdp = answer.sdp
                }
            ));
        }

        /// <summary>
        /// Helper that connects all the peer connection events to their appropriate handlers/functions
        /// </summary>
        private void connectEventHandlers()
        {
            // Audio format negotiation callback
            pc.OnAudioFormatsNegotiated += audioFormatsNegotiated;

            // Connection state change callback
            pc.onconnectionstatechange += connectionStateChange;

            // Debug Stuff
            pc.OnReceiveReport += (re, media, rr) =>
            {
                Serilog.Log.Logger.Verbose("RTCP report received {Media} from {RE}", media, re);
                Serilog.Log.Logger.Verbose(rr.GetDebugSummary());
            };
            pc.OnSendReport += (media, sr) =>
            {
                Serilog.Log.Logger.Verbose("RTCP report sent for {Media}", media);
                Serilog.Log.Logger.Verbose(sr.GetDebugSummary());
            };
            pc.GetRtpChannel().OnStunMessageSent += (msg, ep, isRelay) =>
            {
                Serilog.Log.Logger.Verbose("STUN {MessageType} sent to {Endpoint}", msg.Header.MessageType, ep);
            };
            pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) =>
            {
                Serilog.Log.Logger.Verbose("STUN {MessageType} received from {Endpoint}", msg.Header.MessageType, ep);
                //Log.Verbose(msg.ToString());
            };
            pc.oniceconnectionstatechange += (state) =>
            {
                Serilog.Log.Verbose("ICE connection state change to {ICEState}", state);
            };
            pc.OnTimeout += (mediaType) =>
            {
                Serilog.Log.Logger.Error("RTP timeout for {mediaType}", mediaType);
            };
            pc.OnRemoteDescriptionChanged += (mediaType) =>
            {
                Serilog.Log.Logger.Warning("Remote SDP changed, now {sdp}", mediaType);
            };

            // Detailed logging for debugging the hangup
            pc.GetRtpChannel().OnRTPDataReceived += (port, ep, buffer) =>
            {
                if (buffer.Length >= 12)
                {
                    // Very minimal RTP header parse
                    ushort seq = (ushort)((buffer[2] << 8) | buffer[3]);
                    uint ts = (uint)((buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7]);
                    uint ssrc = (uint)((buffer[8] << 24) | (buffer[9] << 16) | (buffer[10] << 8) | buffer[11]);

                    Serilog.Log.Logger.Debug("Raw RTP: SSRC={SSRC} Seq={Seq} TS={TS}", ssrc, seq, ts);
                }
            };

            // RTP Samples callback
            pc.OnRtpPacketReceived += rtpPacketHandler;
        }

        /// <summary>
        /// Create a new peer connection to a WebRTC endpoint and configure the audio tracks
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public Task<RTCPeerConnection> CreatePeerConnection()
        {
            Serilog.Log.Logger.Debug("New client connected to RTC endpoint, creating peer connection");
            
            // Create new peer connection
            pc = new RTCPeerConnection(null);

            // Debug print of supported audio formats
            Serilog.Log.Logger.Verbose("Client supported formats:");
            foreach (var format in RxEncoder.SupportedFormats)
            {
                Serilog.Log.Logger.Verbose("{FormatName}", format.FormatName);
            }

            // Make sure we support the desired codec
            if (!RxEncoder.SupportedFormats.Any(f => f.FormatName == Codec))
            {
                Serilog.Log.Logger.Error("Specified format {SpecFormat} not supported by audio encoder!", Codec);
                throw new ArgumentException("Invalid codec specified!");
            }

            // Identify the proper format
            AudioFormat fmt = RxEncoder.SupportedFormats.Find(f => f.FormatName == Codec);

            // Set send-only or send-recieve mode based on whether we're RX only or not
            if (!RxOnly)
            {
                audioTrack = new MediaStreamTrack(fmt, MediaStreamStatusEnum.SendRecv);
                pc.addTrack(audioTrack);
                Serilog.Log.Logger.Debug("Added send/recv audio track to peer connection");
            } 
            else
            {
                audioTrack = new MediaStreamTrack(fmt, MediaStreamStatusEnum.SendOnly);
                pc.addTrack(audioTrack);
                Serilog.Log.Logger.Debug("Added send-only audio track to peer connection");
            }

            return Task.FromResult(pc);
        }

        /// <summary>
        /// Restarts the peer connection
        /// </summary>
        /// <param name="reason">the reason for the connection restart</param>
        /// <returns></returns>
        private async Task RestartPeerConnection(string reason)
        {
            try
            {
                Serilog.Log.Logger.Warning("Restarting WebRTC peer connection: {reason:l}", reason);

                pc?.Close(reason);
                pc?.Dispose();

                srtpFailureSink.Reset();

                await initPeerConnection();

                // Send a new offer
                RTCSessionDescriptionInit offer = pc.createOffer();
                await pc.setLocalDescription(offer);
                Send(JsonSerializer.Serialize(new
                {
                    type = "offer",
                    sdp = offer.sdp
                }));
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "Error restarting peer connection");
                throw;
            }
        }

        /// <summary>
        /// Handler for RTC connection state chagne
        /// </summary>
        /// <param name="state">the new connection state</param>
        private async void connectionStateChange(RTCPeerConnectionState state)
        {
            Serilog.Log.Logger.Information("Peer connection state change to {PCState}.", state);

            if (state == RTCPeerConnectionState.failed)
            {
                Serilog.Log.Logger.Error("Peer connection failed");
                Serilog.Log.Logger.Debug("Closing peer connection");
                pc.Close("Connection failed");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                Serilog.Log.Logger.Debug("WebRTC connection closed");
                if (OnClose != null)
                {
                    OnWebRTCConnect(this, EventArgs.Empty);
                }
            }
            else if (state == RTCPeerConnectionState.connected)
            {
                Serilog.Log.Logger.Debug("WebRTC connection opened");
                if (OnWebRTCConnect != null)
                {
                    OnWebRTCConnect(this, EventArgs.Empty);
                }
            }
        }

        public void Stop(string reason)
        {
            Serilog.Log.Logger.Warning("Stopping WebRTC with reason {Reason}", reason);
            if (pc != null)
            {
                //Log.Logger.Information($"Closing WebRTC peer connection to {pc.AudioDestinationEndPoint.ToString()}");
                pc.Close(reason);
                pc = null;
            }
            else
            {
                Serilog.Log.Logger.Debug("No WebRTC peer connections to close");
            }
            // Stop the watchdog timers
            txSampleTimer.Stop();
            rxSampleTimer.Stop();
        }
    }
}
