using Concentus;
using Microsoft.Extensions.Logging;
using NAudio;
using NAudio.Dsp;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Timers;
using WebSocketSharp;

namespace rc2_core
{
    /// <summary>
    /// Logging sink used to detect errors/exceptions within SipSorcery and restart the WebRTC peer connection
    /// </summary>
    public class SrtpFailureSink : ILogEventSink
    {
        private int failCount = 0;
        private bool tracking = true;

        private const int failThresh = 3;

        public event Action<string> OnFailure;
        
        public void Emit(LogEvent ev)
        {
            // Ignore if we're not tracking
            if (!tracking) { return; }

            // Check if we had an SRTP failure
            if (ev.MessageTemplate.Text.Contains("SRTP unprotect failed"))
            {
                failCount++;
            }

            // If we exceeded the threshold, fire the handler and stop tracking
            if (failCount > failThresh)
            {
                tracking = false;
                OnFailure?.Invoke("SRTP failures exceeded threshold");
            }
        }

        /// <summary>
        /// Reset the failure counters
        /// </summary>
        public void Reset()
        {
            failCount = 0;
            tracking = true;
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
        public int TxAudioSamplerate;

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

        // WebRTC perfect negotiation flags
        private bool polite = true;
        private bool makingOffer = false;
        private bool ignoreOffer = false;

        // Flag whether our radio is RX only
        public bool RxOnly {get; set;} = false;

        // Event for when the WebRTC connection connects
        public event EventHandler OnWebRTCConnect;

        // Event for when the WebRTC connection closes
        public event EventHandler OnWebRTCClose;

        // Callback for receiving audio from the peer connection
        public Action<short[]> TxCallback;

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
            public string type { get; set; }
            public string sdp { get; set; }
            public string candidate { get; set; }
            public string sdpMid { get; set; }
            public ushort? sdpMLineIndex { get; set; }
        }

        public WebRTCPeer()
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
            }

            // Default samplerate
            TxAudioSamplerate = 8000;

            // Enable hook for SRTP failure monitoring
            srtpFailureSink = new SrtpFailureSink();
            srtpFailureSink.OnFailure += (reason) => RestartPeerConnection(reason);
            Serilog.Core.Logger srtpLogger = new LoggerConfiguration().WriteTo.Sink(srtpFailureSink).CreateLogger();

            // Enable SipSorcery Logging
            Microsoft.Extensions.Logging.ILoggerFactory factory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(Serilog.Log.Logger);
                builder.AddSerilog(srtpLogger);
            });
            SIPSorcery.LogFactory.Set(factory);

            // Logging
            Serilog.Log.Logger.Debug("Created new WebRTCPeer");
        }

        protected override void OnOpen()
        {
            Serilog.Log.Logger.Debug("WebRTC websocket connection opened");
            createPeerConnection();
            connectEventHandlers();
            configureAudio();
        }

        /// <summary>
        /// Create a new peer connection to a WebRTC endpoint and configure the audio tracks
        /// </summary>
        private void createPeerConnection()
        {
            Serilog.Log.Logger.Debug("Creating WebRTC peer connection");

            // Create new peer connection
            pc = new RTCPeerConnection(null);
        }

        /// <summary>
        /// Helper that connects all the peer connection events to their appropriate handlers/functions
        /// </summary>
        private void connectEventHandlers()
        {
            Serilog.Log.Logger.Debug("Binding event handlers for WebRTC peer connection");

            // Negotiation needed handler
            pc.onnegotiationneeded += async () => { await negotiationNeeded(); };

            // Audio format negotiation callback
            pc.OnAudioFormatsNegotiated += audioFormatsNegotiated;

            // Connection state change callback
            pc.onconnectionstatechange += connectionStateChange;

            // ICE candidate handler for local ICE candidates
            pc.onicecandidate += (candidate) =>
            {
                if (candidate != null)
                {
                    Serilog.Log.Logger.Verbose("Got new ICE candidate: {candidate}, sending to console", candidate);
                    Send(JsonSerializer.Serialize(new
                    {
                        type = "candidate",
                        candidate = $"candidate:{candidate.candidate}",
                        sdpMid = candidate.sdpMid,
                        sdpMLineIndex = candidate.sdpMLineIndex
                    }));
                }
            };

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

            // Detailed logging for individual WebRTC RTP packets, keep disabled unless needed
            /**pc.GetRtpChannel().OnRTPDataReceived += (port, ep, buffer) =>
            {
                if (buffer.Length >= 12)
                {
                    // Very minimal RTP header parse
                    ushort seq = (ushort)((buffer[2] << 8) | buffer[3]);
                    uint ts = (uint)((buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7]);
                    uint ssrc = (uint)((buffer[8] << 24) | (buffer[9] << 16) | (buffer[10] << 8) | buffer[11]);

                    Serilog.Log.Logger.Verbose("Raw RTP: SSRC={SSRC} Seq={Seq} TS={TS}", ssrc, seq, ts);
                }
            };*/

            // RTP Samples callback
            pc.OnRtpPacketReceived += rtpPacketHandler;
        }

        /// <summary>
        /// Handler fired when WebRTC audio negotiation is needed
        /// </summary>
        private async Task negotiationNeeded()
        {
            Serilog.Log.Logger.Verbose("WebRTC negotiation needed, sending new offer to console");
            try
            {
                // Set the flag
                makingOffer = true;
                // Create and set a new offer
                RTCSessionDescriptionInit offer = pc.createOffer();
                await pc.setLocalDescription(offer);
                // Send the offer
                Send(JsonSerializer.Serialize(new
                {
                    type = "offer",
                    sdp = offer.sdp,
                }));
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "Caught exception while creating and sending local offer");
            }
            finally
            {
                makingOffer = false;
            }
        }

        /// <summary>
        /// Handler for RTC connection state chagne
        /// </summary>
        /// <param name="state">the new connection state</param>
        private void connectionStateChange(RTCPeerConnectionState state)
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
                if (OnWebRTCClose != null)
                {
                    OnWebRTCClose(this, EventArgs.Empty);
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

        /// <summary>
        /// Configure audio codecs and add the new track to the peer connection
        /// </summary>
        /// <exception cref="ArgumentException"></exception>
        private void configureAudio()
        {
            Serilog.Log.Logger.Debug("Configuring local WebRTC media tracks");

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
        }

        /// <summary>
        /// Message handler for messages received from the WebRTC websocket channel
        /// </summary>
        /// <param name="e"></param>
        /// <exception cref="InvalidDataException"></exception>
        protected override async void OnMessage(MessageEventArgs e)
        {
            try
            {
                Serilog.Log.Logger.Verbose("Got WebRTC message: {data}", e.Data);

                SignalingMessage? msg = JsonSerializer.Deserialize<SignalingMessage>(e.Data);

                if (msg == null)
                    throw new InvalidDataException($"Unable to deserialize JSON: {e.Data}");

                if (msg.type != null)
                {
                    switch (msg.type)
                    {
                        case "offer":
                            await handleRemoteOffer(msg.sdp);
                            break;
                        case "answer":
                            handleRemoteAnswer(msg.sdp);
                            break;
                        default:
                            Serilog.Log.Logger.Warning("Unknown signaling message type: {type}", msg.type);
                            break;
                    }
                } else if (msg.candidate != null)
                {
                    handleRemoteCandidate(msg);
                }
            }
            catch (JsonException ex)
            {
                Serilog.Log.Logger.Error(ex, "Got error while deserializing JSON data!");
                Serilog.Log.Logger.Error("Data: {data:l}", e.Data);
                throw;
            }
        }

        /// <summary>
        /// Handler for a received remote SDP offer
        /// </summary>
        /// <param name="sdp">the SDP string</param>
        /// <returns></returns>
        private async Task handleRemoteOffer(string sdp)
        {
            Serilog.Log.Logger.Verbose("Received remote SDP offer: {sdp:l}", sdp);

            // Create an offer object from the SDP received
            RTCSessionDescriptionInit offer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = sdp
            };

            // Determine if there is an offer collision and if we should ignore it
            bool collision = (pc.signalingState != RTCSignalingState.stable && makingOffer);
            ignoreOffer = !polite && collision;

            // Ignore the offer if we're impolite (normally not used)
            if (ignoreOffer)
            {
                Serilog.Log.Logger.Warning("Ignoring remote offer due to collision (impolite side)");
                return;
            }

            // If we're polite (default), rollback our local description
            if (collision)
            {
                Serilog.Log.Logger.Warning("Offer collision detected, rolling back (polite side)");
                await pc.setLocalDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.rollback
                });
            }

            // Set the remote description from the offer
            pc.setRemoteDescription(offer);

            // Create a session description answer
            RTCSessionDescriptionInit answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            // Send the answer
            Send(JsonSerializer.Serialize(new
            {
                type = "answer",
                sdp = answer.sdp
            }));
        }

        /// <summary>
        /// Handler for a received remote SDP answer
        /// </summary>
        /// <param name="sdp"></param>
        /// <returns></returns>
        private void handleRemoteAnswer(string sdp)
        {
            Serilog.Log.Logger.Verbose("Received remote SDP answer: {sdp:l}", sdp);

            // Create a new answer object from the received SDP
            RTCSessionDescriptionInit answer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = sdp
            };
            // Set the remote description based on this answer
            pc.setRemoteDescription(answer);
        }

        /// <summary>
        /// Handler for a received remote ICE candidate
        /// </summary>
        /// <param name="msg"></param>
        private void handleRemoteCandidate(SignalingMessage msg)
        {
            // Ensure we got a valid candidate
            if (string.IsNullOrWhiteSpace(msg.candidate))
            {
                Serilog.Log.Logger.Warning("Received empty remote ICE candidate!");
                return;
            }

            // Create a new candidate object
            RTCIceCandidateInit candidate = new RTCIceCandidateInit
            {
                candidate = msg.candidate,
                sdpMid = msg.sdpMid,
                sdpMLineIndex = msg.sdpMLineIndex ?? 0
            };

            // Add it to the peer connection
            pc.addIceCandidate(candidate);

            Serilog.Log.Logger.Debug("Added new remote ICE candidate to WebRTC peer connection");
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

                try
                {
                    pc?.Close(reason);
                    pc?.Dispose();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Logger.Error(ex, "Caught exception while closing stale WebRTC connection");
                }

                // Stop the audio monitoring
                Serilog.Log.Logger.Verbose("Stopping sample watchdog timers");
                txSampleTimer.Stop();
                rxSampleTimer.Stop();

                // Reset negotiation flags
                makingOffer = false;
                ignoreOffer = false;

                // Reset the failure detection sink
                srtpFailureSink.Reset();

                // Create a new peer connection
                pc = new RTCPeerConnection(null);

                // Reconnect everything
                connectEventHandlers();
                configureAudio();

                // Create and send new offer
                Serilog.Log.Logger.Verbose("Creating new WebRTC offer and sending to console");
                RTCSessionDescriptionInit offer = pc.createOffer(null);
                await pc.setLocalDescription(offer);
                Send(JsonSerializer.Serialize(new
                {
                    type = "offer",
                    sdp = offer.sdp
                }));
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "Caught exception while restarting WebRTC peer connection");
                throw;
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
