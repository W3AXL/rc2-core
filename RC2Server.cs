using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Newtonsoft.Json;
using RadioConsole.Protocol;
using Google.Protobuf;
using Fleck;

namespace rc2_core
{
    public class RC2Server
    {
        /// <summary>
        /// Protocol version, should be incremeneted whenever breaking changes are made
        /// </summary>
        public const uint ProtocolVersion = 1;

        /// <summary>
        /// Websocket Listen Address
        /// </summary>
        private readonly IPAddress address;
        /// <summary>
        /// Websocket Listen port
        /// </summary>
        private readonly int port;

        /// <summary>
        /// Websocket server
        /// </summary>
        private WebSocketServer? wss {  get; set; }

        /// <summary>
        /// List of currently connected websocket sessions
        /// </summary>
        private readonly List<IWebSocketConnection> sessions = new();

        private readonly object sessionsLock = new();

        /// <summary>
        /// Internal RC2 radio object for status tracking
        /// </summary>
        private Radio radio;

        /// <summary>
        /// Internal audio bridge object for audio transfer
        /// </summary>
        private AudioBridge audioBridge;

        /// <summary>
        /// Internal flag indicatig that the console is ready to accept audio
        /// </summary>
        internal bool ConsoleReady { get; private set; } = false;

        /// <summary>
        /// Ping timer for the socket keepalive test
        /// </summary>
        private readonly System.Timers.Timer pingTimer = new System.Timers.Timer(1000);

        /// <summary>
        /// A random 32bit number to correlate pings
        /// </summary>
        private uint pingNonce = 0;

        /// <summary>
        /// Tracker for number of missed pongs
        /// </summary>
        private uint missedPongs = 0;

        /// <summary>
        /// Max number of missed pongs before the connection is considered failed
        /// </summary>
        private const uint MaxMissedPongs = 3;

        /// <summary>
        /// List of networks allowed to talk to this server instance
        /// </summary>
        private List<IPNetwork> allowedNetworks;

        /// <summary>
        /// Create a new instance of a RadioConsole2 Websocket/WebRTC server
        /// </summary>
        /// <param name="address">listen address for the server</param>
        /// <param name="port">listen port for the server</param>
        /// <param name="_radio">parent radio instance</param>
        /// <param name="txAudioCallback">callback to handle TX audio samples from WebRTC connection</param>
        /// <param name="radioSampleRate">sample rate to use for the radio's TX & RX audio connection</param>
        /// <param name="rtcFormatCallback">callback when WebRTC audio formats are negotiated</param>
        public RC2Server(IPAddress address, int port, Radio _radio, int radioSampleRate, List<IPNetwork> allowedNetworks)
        {
            // Store server info
            this.address = address;
            this.port = port;

            // Store the radio connection
            radio = _radio;
            
            // Store allowed networks
            this.allowedNetworks = allowedNetworks;

            // Create the audio bridge
            audioBridge = new AudioBridge(
                radioSampleRate,
                48000,
                radioSampleRate,
                radio.RxOnly
            );

            // Set up TX audio callback (with an action invoke to ensure it's bound properly at runtime)
            audioBridge.TxAudioCallback += (samples, rate) => radio.OnTxAudio?.Invoke(samples, rate);
            // Set up RX frame sending
            audioBridge.OnEncodedRxFrame += (frame) =>
            {
                if (ConsoleReady)
                {
                    SendAudioFrame(frame);
                }
            };
            // Set up handler for TX watchdog
            audioBridge.OnTxWatchdogTripped += () =>
            {
                Log.Logger.Error("No TX audio for 500ms during active TX, stopping transmit");
                radio.SetTransmit(false);
            };

            // Bind radio status callback
            radio.OnStatusUpdated += SendRadioStatus;
        }

        /// <summary>
        /// Start the RC2 daemon server
        /// </summary>
        public void Start()
        {
            // Prepare the host string, properly parsing the IPV4/IPV6 "any" hosts
            string host = ( address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) ? "0.0.0.0" : address.ToString();

            // Create the websocket connection
            wss = new WebSocketServer($"ws://{host}:{port}");

            Log.Logger.Information($"Starting RC2 daemon, server listening on {host}:{port}");

            // Start the connection and map our handlers
            wss.Start(socket =>
            {
                // On open, grab the sessions lock and add the new session to our list
                socket.OnOpen = () =>
                {
                    lock (sessionsLock) sessions.Add(socket);
                    OnConsoleOpen();  
                };
                // On close, remove the session from the list
                socket.OnClose = () =>
                {
                    lock (sessionsLock) sessions.Remove(socket);
                    pingTimer.Stop();
                    OnConsoleClose("Connection closed");
                };
                // Error handler
                socket.OnError = ex => OnConsoleError(ex.Message);
                // Binary message handler
                socket.OnBinary = data => OnConsoleData(data);
                // We should expect only binary messages
                socket.OnMessage = _ => Log.Logger.Warning("Got unexpected text message from console, ignoring");
            });

            // Setup ping timer but don't start until successful handshake
            pingTimer.Elapsed += (s, e) => SendPing();
        }

        /// <summary>
        /// Stop the RC2 daemon server, providing a reason to the connected console client
        /// </summary>
        /// <param name="reason">the reason for stopping</param>
        public void Stop(string reason)
        {
            pingTimer.Stop();
            wss?.Dispose();
            lock (sessionsLock) sessions.Clear();
        }

        /// <summary>
        /// Fired when the websocket connection is opened
        /// </summary>
        private void OnConsoleOpen()
        {
            Log.Logger.Debug("Console websocket connected, sending Hello");
            SendHello();
        }

        /// <summary>
        /// Fired when the websocket connection is closed
        /// </summary>
        /// <param name="reason"></param>
        private void OnConsoleClose(string reason)
        {
            // Stop TX, just in case we're transmitting
            radio.SetTransmit(false);
            // Log
            Log.Logger.Warning("Websocket connection closed, reason {reason}", reason);
        }

        /// <summary>
        /// Fired when the websocket encounters an error
        /// </summary>
        /// <param name="message"></param>
        private void OnConsoleError(string message)
        {
            Log.Logger.Error("Websocket encountered an error: {error}", message);   
        }

        /// <summary>
        /// Primary handler for incoming communications from the console side
        /// </summary>
        /// <param name="data"></param>
        private void OnConsoleData(byte[] data)
        {
            // Try to parse the raw data out to a protocol envelope
            Envelope env;
            try
            {
                env = Envelope.Parser.ParseFrom(data);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to parse envelope from console message");
                return;
            }

            // Initially, we filter all messages except HelloAcks until the console is ready
            bool isHelloAck = env.PayloadCase == Envelope.PayloadOneofCase.Control && env.Control.BodyCase == ControlMessage.BodyOneofCase.HelloAck;
            if (!ConsoleReady && !isHelloAck)
            {
                Log.Logger.Warning("Ignoring {messageType} message from console, not yet ready", env.PayloadCase == Envelope.PayloadOneofCase.Control ? env.Control.BodyCase.ToString() : "audio");
                return;
            }

            // Handle control vs audio messages
            switch (env.PayloadCase)
            {
                case Envelope.PayloadOneofCase.Control:
                    HandleControl(env.Control);
                    break;
                case Envelope.PayloadOneofCase.Audio:
                    // Ensure this is a mic frame (only type of frame that the console should send)
                    if (env.Audio.Source == AudioSource.Mic)
                    {
                        audioBridge.HandleTxFrame(env.Audio);
                    }
                    else
                    {
                        Log.Logger.Warning("Got non-Mic audio from console, ignoring");
                    }
                    break;
            }
        }

        /// <summary>
        /// Handler for control messages received from the console
        /// </summary>
        /// <param name="msg">the control message</param>
        private void HandleControl(ControlMessage msg)
        {
            switch (msg.BodyCase)
            {
                case ControlMessage.BodyOneofCase.HelloAck:
                    HandleHelloAck(msg.HelloAck);
                    break;
                case ControlMessage.BodyOneofCase.RadioCommand:
                    HandleRadioCommand(msg.RadioCommand);
                    break;
                case ControlMessage.BodyOneofCase.NetworkQuery:
                    SendNetworkConfig();
                    break;
                case ControlMessage.BodyOneofCase.Ping:
                    SendPong(msg.Ping.Nonce);
                    break;
                case ControlMessage.BodyOneofCase.Pong:
                    HandlePong(msg.Pong.Nonce);
                    break;
            }
        }

        /// <summary>
        /// Handler for radio commands received from the console
        /// </summary>
        /// <param name="cmd">the command to process</param>
        private void HandleRadioCommand(RadioCommand cmd)
        {
            // A status query or a reset command don't expect an ACK, so we handle them first
            if (cmd.Command == RadioCommandType.Query)
            {
                SendRadioStatus();
                return;
            }
            if (cmd.Command == RadioCommandType.Reset)
            {
                Serilog.Log.Logger.Warning("Got reset command from console, resetting radio");
                radio.Stop();
                radio.Start();
                return;
            }

            // This is a clever way to handle parsing a bool back from multiple functions that all return bool
            bool ok = cmd.Command switch
            {
                // Button Commands
                RadioCommandType.ButtonPress => radio.PressButton(cmd.Softkey),
                RadioCommandType.ButtonRelease => radio.ReleaseButton(cmd.Softkey),
                RadioCommandType.ButtonToggle => radio.ToggleButton(cmd.Softkey),
                // Channel Commands
                RadioCommandType.ChanUp => radio.ChangeChannel(false),
                RadioCommandType.ChanDown => radio.ChangeChannel(true),
                // TX Commands
                RadioCommandType.StartTx => radio.SetTransmit(true),
                RadioCommandType.StopTx => radio.SetTransmit(false),
                // Unhandled catch-all
                _ => LogUnhandled(cmd.Command),
            };

            if (ok)
                SendAck(cmd.Command, cmd.RequestId);
            else
                SendNack(cmd.Command, cmd.RequestId, "Command failed");
        }

        /// <summary>
        /// Small helper class to log any unhandled control messages
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        private static bool LogUnhandled(RadioCommandType cmd)
        {
            Serilog.Log.Logger.Warning("Unhandled radio command {cmd}", cmd);
            return false;
        }

        /// <summary>
        /// Get the current unix time in microseconds, used for timestamping the envelope messages
        /// </summary>
        /// <returns></returns>
        private static ulong NowMicros() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        /// <summary>
        /// Send an envelope message
        /// </summary>
        /// <param name="env">the envelope to send</param>
        private void Send(Envelope env)
        {
            // Prepare the byte data
            byte[] data = env.ToByteArray();
            // Prepare a list of targets to send the message to
            List<IWebSocketConnection> targets;
            lock(sessionsLock) targets = sessions.ToList();
            // Send to each connection
            foreach (IWebSocketConnection conn in targets)
            {
                if (conn.IsAvailable) conn.Send(data);
            }
        }

        /// <summary>
        /// Send a Hello message to the console client to identify this daemon
        /// </summary>
        internal void SendHello()
        {
            // If we're sending a hello, we're not ready
            ConsoleReady = false;

            // Prepare the hello message
            Hello hello = new Hello
            {
                ProtocolVersion = ProtocolVersion,
                DaemonName = radio.Name,
                AudioCodec = AudioCodec.Opus,
                RxSampleRateHz = (uint)audioBridge.RxSampleRateHz,
                FrameDurationMs = OpusFrameConfig.FrameDurationMs
            };
            // Set TX sample rate if we have a tx configuration
            if (audioBridge.TxSampleRateHz.HasValue)
                hello.TxSampleRateHz = (uint)audioBridge.TxSampleRateHz;

            // Log
            Log.Logger.Debug("Sending Hello");
            Log.Logger.Verbose("RX {rx} Hz, TX {tx} Hz, Frame = {frame} ms", hello.RxSampleRateHz, hello.TxSampleRateHz, hello.FrameDurationMs);

            // Send
            Send(new Envelope { TimestampUs = NowMicros(), Control = new ControlMessage { Hello = hello }} );
        }

        /// <summary>
        /// Handle the Hello ACK message from the client
        /// </summary>
        /// <param name="ack">the ack message</param>
        internal void HandleHelloAck(HelloAck ack)
        {
            if (!ack.Accepted)
            {
                Log.Logger.Error("console rejected Hello: {reason} - closing connection", ack.Reason);
                Stop("Console rejected Hello");
                return;
            }
            Log.Logger.Information("Connected to console, audio session ready");
            // Send initial status
            SendRadioStatus();
            // Start ping timer
            pingTimer.Start();
            // We're ready
            ConsoleReady = true;
        }

        /// <summary>
        /// Send the radio status to the console
        /// </summary>
        public void SendRadioStatus()
        {
            // Log
            Log.Logger.Debug("Sending radio status");
            // Send
            Send(new Envelope
            {
                TimestampUs = NowMicros(),
                Control = new ControlMessage { RadioStatus = radio.Status }
            });
            // Check if the radio is transmitting and update the TX audio state accordingly
            audioBridge.TxActive = radio.Status.State == RadioState.Transmitting;
        }

        /// <summary>
        /// Send an ACK in response to a radio command
        /// </summary>
        /// <param name="inResponseTo">the command type the ACK is responding to</param>
        /// <param name="requestId">the radio command's request id</param>
        public void SendAck(RadioCommandType inResponseTo, uint requestId) =>
            Send(new Envelope
            {
               TimestampUs = NowMicros(),
               Control = new ControlMessage
               {
                   Ack = new Ack
                   {
                       InResponseTo = inResponseTo,
                       RequestId = requestId
                   }
               } 
            });

        /// <summary>
        /// Send a NACK in response to a radio command
        /// </summary>
        /// <param name="inResponseTo">the command type the ACK is responding to</param>
        /// <param name="requestId">the radio command's request id</param>
        /// <param name="reason">the reason for the NACK</param>
        public void SendNack(RadioCommandType inResponseTo, uint requestId, string reason) =>
            Send(new Envelope
            {
                TimestampUs = NowMicros(),
                Control = new ControlMessage
                {
                    Nack = new Nack
                    {
                        InResponseTo = inResponseTo,
                        RequestId = requestId,
                        Reason = reason
                    }
                }
            });

        /// <summary>
        /// Send the network config for this deamon to the console client
        /// 
        /// Currently, this just sends a list of allowed networks
        /// </summary>
        public void SendNetworkConfig()
        {
            Log.Logger.Debug("Sending network configuration");
            // Prepare the list of allowed networks
            NetworkConfig netConfig = new NetworkConfig();
            foreach (IPNetwork network in allowedNetworks)
            {
                // Convert prefix length to netmask string
                UInt32 netmask = (uint)(0xFFFFFFFFU & -(1U << (32 - network.PrefixLength)));
                string netmask_string = $"{(netmask >> 24) & 0xFF}.{(netmask >> 16) & 0xFF}.{(netmask >> 8) & 0xFF}.{netmask & 0xFF}";
                netConfig.AllowedNetworks.Add(new AllowedNetwork
                {
                   BaseAddress = network.BaseAddress.ToString(),
                   SubnetMask = netmask_string
                });
            }
            // Send
            Send(new Envelope
            {
                TimestampUs = NowMicros(),
                Control = new ControlMessage
                {
                    NetworkConfig = netConfig
                }
            });
        }

        /// <summary>
        /// Send a ping to the console client
        /// </summary>
        private void SendPing()
        {
            // We increment missed pongs here, and the HandlePong method will clear it once we receive a pong
            missedPongs++;
            if (missedPongs > MaxMissedPongs && radio.Status.State != RadioState.Disconnected)
            {
                Log.Logger.Error("Console unresponsive ({count} missed pongs) - stopping transmit", missedPongs);
                radio.SetTransmit(false);
            }
            Send(new Envelope
            {
                TimestampUs = NowMicros(),
                Control = new ControlMessage
                {
                    Ping = new Ping { Nonce = ++pingNonce }
                }
            });
        }

        /// <summary>
        /// Send a pong in response to a ping
        /// </summary>
        /// <param name="nonce">the ping's nonce</param>
        internal void SendPong(uint nonce)
        {
            Send(new Envelope
            {
                TimestampUs = NowMicros(),
                Control = new ControlMessage
                {
                    Pong = new Pong
                    {
                        Nonce = nonce
                    }
                }
            });
        }

        /// <summary>
        /// Handle a received pong and reset the missed pongs counter
        /// </summary>
        /// <param name="nonce">the pong's nonce, currently unused</param>
        internal void HandlePong(uint nonce)
        {
            // Reset missed pongs
            missedPongs = 0;
        }

        /// <summary>
        /// Send PCM16 audio samples for encoding and emitting to the client
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="samplerate"></param>
        public void SendRxPCM16Samples(short[] samples, uint samplerate)
        {
            audioBridge.SendRxSamples(samples, samplerate);
        }

        /// <summary>
        /// Send an audio frame to the connected console
        /// </summary>
        /// <param name="frame"></param>
        internal void SendAudioFrame(AudioFrame frame)
        {
            Send(new Envelope
            {
                TimestampUs = NowMicros(),
                Audio = frame
            });
        }
    }
}
