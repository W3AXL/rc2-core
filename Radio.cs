using Serilog;
using Newtonsoft.Json;
using System.Net;
using RadioConsole.Protocol;

namespace rc2_core
{
    // Program return codes
    public enum ERRNO : int
    {
        /// <summary>
        /// No errors, normal exit
        /// </summary>
        ENOERROR = 0,
        /// <summary>
        /// Bad command line options provided
        /// </summary>
        EBADOPTIONS = 1,
        /// <summary>
        /// No config file provided
        /// </summary>
        ENOCONFIG = 2,
        /// <summary>
        /// Config file malformed
        /// </summary>
        EBADCONFIG = 3,
        /// <summary>
        /// Unhandled exit code
        /// </summary>
        EUNHANDLED = 99
    }

    /// <summary>
    /// Class for text-replacement lookup objects
    /// </summary>
    public struct TextLookup
    {
        // The text string to match
        public string Match { get; set; }
        // The text string to replace the matched text with
        public string Replace { get; set; }
    }

    /// <summary>
    /// Radio class representing a radio to be controlled by the daemon
    /// </summary>
    public abstract class Radio
    {
        // Radio Name
        private string name = "Radio";
        public string Name
        {
            get { return name; }
        }

        // Radio Description
        private string desc = "RC2 Base Radio Instance";
        public string Desc
        {
            get { return desc; }
        }

        // Whether the radio is RX only
        public bool RxOnly { get; set; }

        // Radio status
        public RadioStatus Status { get; set; }

        // Lookup lists for zone & channel text
        private List<TextLookup> ZoneLookups = new List<TextLookup>();
        private List<TextLookup> ChanLookups = new List<TextLookup>();

        /// <summary>
        /// Action that fires when the radio status has been updated
        /// </summary>
        public Action? OnStatusUpdated;

        /// <summary>
        /// Action that fires when new TX audio samples are available
        /// </summary>
        public Action<short[], int>? OnTxAudio;

        public int RecTimeout { get; set; } = 0;

        // RC2 server instance
        private RC2Server server { get; set; }

        /// <summary>
        /// The delay to wait in between pressing & releasing a button in a ToggleButton command
        /// </summary>
        public int ButtonToggleDelayMs = 200; 

        /// <summary>
        /// Base radio class, does nothing on its own other than instantiate the WebRTC and Websocket connections
        /// </summary>
        /// <param name="name">name of the radio</param>
        /// <param name="desc">description of the radio</param>
        /// <param name="rxOnly">whether the radio is RX only (TX disabled)</param>
        /// <param name="listenAddress">listen address for the radio</param>
        /// <param name="listenPort">listen port for the radio</param>
        /// <param name="consoleSoftkeys">list of softkeys the daemon has been configured to provide</param>
        /// <param name="zoneLookups">list of zone text lookups</param>
        /// <param name="chanLookups">list of channel text lookups</param>
        /// <param name="txAudioSampleRate">sample rate the TX callback expects</param>
        public Radio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            List<IPNetwork> allowedNetworks,
            List<SoftkeyName>? consoleSoftkeys = null,
            List<TextLookup>? zoneLookups = null,
            List<TextLookup>? chanLookups = null,
            int txAudioSampleRate = 48000)
        {
            // Log Print
            Log.Logger.Information($"Creating new RC2 radio {name} ({desc}) listening on {listenAddress}:{listenPort}");

            // Base name
            this.name = name;
            this.desc = desc;

            // Store RX Only
            RxOnly = rxOnly;

            // Create backend server
            server = new RC2Server(listenAddress, listenPort, this, txAudioSampleRate, allowedNetworks);

            // Create status and assign name & description
            Status = new RadioStatus();
            Status.Name = name;
            Status.Description = desc;

            // Populate the status object softkey list
            if (consoleSoftkeys != null) 
            { 
                foreach(SoftkeyName softkeyName in consoleSoftkeys)
                {
                    Status.Softkeys.Add(new Softkey
                    {
                        Name = softkeyName,
                        State = SoftkeyState.Unspecified
                    });
                }
            }
            
            // Save channel & zone text lookups
            if (zoneLookups != null) { ZoneLookups = zoneLookups; }
            if (chanLookups != null) { ChanLookups = chanLookups; }
            Log.Logger.Debug("Loaded {zoneCount} zone text lookups", ZoneLookups.Count);
            ZoneLookups.ForEach((lookup) => {
                Log.Logger.Verbose("    {match} -> {replace}", lookup.Match, lookup.Replace);
            });

            Log.Logger.Debug("Loaded {chanCount} channel text lookups", ChanLookups.Count);
            ChanLookups.ForEach((lookup) => {
                Log.Logger.Verbose("    {match} -> {replace}", lookup.Match, lookup.Replace);
            });
        }

        /// <summary>
        /// Start the radio
        /// </summary>
        /// <param name="reset">Whether to reset the radio or not</param>
        public virtual void Start(bool reset = false)
        {
            Log.Logger.Information($"Starting radio {name}");
            // Start the server
            server.Start();
            // Update the radio status to connecting
            Status.State = RadioState.Connecting;
            // Call the status callback to set up initial status
            RadioStatusCallback();
        }

        /// <summary>
        /// Stop the radio
        /// </summary>
        public virtual void Stop()
        {
            Log.Logger.Information($"Stopping radio {name}");
            // Stop the server
            server.Stop("Radio instance stopped");
        }

        /// <summary>
        /// Callback function called by the interface class, which in turn calls the callback in the main program for reporting status
        /// Confusing, I know
        /// Basically it goes like this (for SB9600) SB9600.StatusCallback() -> Radio.RadioStatusCallback() -> DaemonWebsocket.SendRadioStatus()
        /// </summary>
        public void RadioStatusCallback()
        {
            Log.Logger.Verbose("Got radio status callback from interface");
            // Perform lookups on zone/channel names (radio-control-type agnostic)
            if (ZoneLookups.Count > 0)
            {
                foreach (TextLookup lookup in ZoneLookups)
                {
                    // An empty string for the match indicates we should always replace the zone name with the replacement
                    if (lookup.Match == "")
                    {
                        Log.Logger.Verbose("Empty lookup {replacement} found for zone name, overriding all other lookups", lookup.Replace);
                        Status.ZoneName = lookup.Replace;
                        break;
                    }
                    // Check Zone Text First (for dual-line displays like M3)
                    if (Status.ZoneName.Contains(lookup.Match))
                    {
                        Log.Logger.Verbose("Found zone text {ZoneName} from {Match} in zone text {Text}", lookup.Replace, lookup.Match, Status.ZoneName);
                        Status.ZoneName = lookup.Replace;
                    }
                    else if (Status.ChannelName.Contains(lookup.Match))
                    {
                        Log.Logger.Verbose("Found zone text {ZoneName} from {Match} in channel text {Text}", lookup.Replace, lookup.Match, Status.ChannelName);
                        Status.ZoneName = lookup.Replace;
                    }
                }
            }
            if (ChanLookups.Count > 0)
            {
                foreach (TextLookup lookup in ChanLookups)
                {
                    if (Status.ChannelName.Contains(lookup.Match))
                    {
                        Log.Logger.Verbose("Found channel text {ChannelName} from {Match} in original text {Text}", lookup.Replace, lookup.Match, Status.ChannelName);
                        Status.ChannelName = lookup.Replace;
                    }
                }
            }

            // Finally, call the next callback up
            OnStatusUpdated?.Invoke();
        }

        /// <summary>
        /// Sets transmit state of the connected radio
        /// </summary>
        /// <param name="tx">true to transmit, false to stop</param>
        /// <returns>true on success</returns>
        public abstract bool SetTransmit(bool tx);

        /// <summary>
        /// Change the radio's channel
        /// </summary>
        /// <param name="down"></param>
        /// <returns></returns>
        public abstract bool ChangeChannel(bool down);

        /// <summary>
        /// Press a button
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public abstract bool PressButton(SoftkeyName name);

        /// <summary>
        /// Release a button
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public abstract bool ReleaseButton(SoftkeyName name);

        /// <summary>
        /// Press a button, wait 200 ms, then release it
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool ToggleButton(SoftkeyName name)
        {
            bool ok = PressButton(name);
            Thread.Sleep(ButtonToggleDelayMs);
            return ok && ReleaseButton(name);
        }

        /// <summary>
        /// Send PCM16 samples to the server for encoding and transmission to the console
        /// </summary>
        /// <param name="samples">array of PCM16 samples</param>
        public void SendRxPCM16Samples(short[] samples, uint samplerate)
        {
            server.SendRxPCM16Samples(samples, samplerate);
        }
    }
}
