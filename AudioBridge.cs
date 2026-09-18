using System;
using System.Timers;
using Concentus;
using Concentus.Enums;
using Google.Protobuf;
using NAudio.Dsp;
using RadioConsole.Protocol;
using Serilog;

namespace rc2_core
{
    /// <summary>
    /// Configuration for an Opus frame (codec, sample rate, etc)
    /// We use this as part of the initial daemon-console handshake
    /// to negoatiate the proper rates n frame sizes n such
    /// </summary>
    public class OpusFrameConfig
    {
        /// <summary>
        /// Frame duration in milliseconds
        /// </summary>
        public const int FrameDurationMs = 20;  // We hardcode frame duration to be 20ms since that's standard for most VOIP apps

        /// <summary>
        /// Array of valid OPUS sample rates
        /// </summary>
        private static readonly int[] ValidOpusSampleRates = { 8000, 12000, 16000, 24000, 48000 };

        /// <summary>
        /// The samplerate for this config, set on instance creation only
        /// </summary>
        public int SampleRateHz { get; }

        /// <summary>
        /// The length of the frame in samples, based on the samplerate and frame duration
        /// </summary>
        public int FrameSampleLength => SampleRateHz * FrameDurationMs / 1000;

        /// <summary>
        /// Instantiate a new Opus frame config
        /// </summary>
        /// <param name="sampleRateHz">the samplerate for this frame config</param>
        public OpusFrameConfig(int sampleRateHz)
        {
            // Validate the sample rate
            if (!ValidOpusSampleRates.Contains(sampleRateHz))
            {
                throw new ArgumentException($"Invalid OPUS samplerate {sampleRateHz} Hz, must be one of {string.Join(", ", ValidOpusSampleRates)}");
            }
            // Store it
            SampleRateHz = sampleRateHz;
        }
    }

    /// <summary>
    /// This class is used to store and forward audio samples at the desired
    /// frame size. It will take in samples of any size and only output samples
    /// once a sufficient number is present in the buffer
    /// </summary>
    internal class SampleAccumulator
    {
        /// <summary>
        /// The internal buffer to store samples
        /// </summary>
        private readonly short[] buffer;
        /// <summary>
        /// The desired output frame size, in samples
        /// </summary>
        private readonly int frameSize;
        /// <summary>
        /// Tracker for how many samples we have filled in the buffer so far
        /// </summary>
        private int filled = 0;

        /// <summary>
        /// Instantiate a new sample accumulator
        /// </summary>
        /// <param name="frameSize">the output frame size, in samples</param>
        public SampleAccumulator(int frameSize)
        {
            this.frameSize = frameSize;
            buffer = new short[frameSize];
        }

        /// <summary>
        /// Push samples to the accumulator, firing the onFrame callback if the buffer is filled
        /// </summary>
        /// <param name="samples">the samples to push</param>
        /// <param name="onFrame">the callback function to fire if the buffer is filled</param>
        public void Push(ReadOnlySpan<short> samples, Action<short[]> onFrame)
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                // Figure out how many samples we need to copy to get a full frame
                int toCopy = Math.Min(frameSize - filled, samples.Length - offset);
                // Copy the samples over
                samples.Slice(offset, toCopy).CopyTo(buffer.AsSpan(filled));
                // Increment our counters
                filled += toCopy;
                offset += toCopy;

                // If we filled the buffer, push it
                if (filled == frameSize)
                {
                    onFrame(buffer);
                    filled = 0;
                }
            }
        }
    }

    public class AudioBridge
    {
        /// <summary>
        /// OPUS frame configuration for the RX (speaker) audio
        /// </summary>
        private readonly OpusFrameConfig rxFrameConfig;
        /// <summary>
        /// OPUS frame configuration for the TX (mic) audio
        /// </summary>
        private readonly OpusFrameConfig? txFrameConfig;

        /// <summary>
        /// Opus RX encoder for outgoing audio
        /// </summary>
        private readonly IOpusEncoder rxEncoder;
        /// <summary>
        /// Opus TX decoder for incoming audio
        /// </summary>
        private readonly IOpusDecoder? txDecoder;

        /// <summary>
        /// The sample accumulator for outgoing RX (speaker) samples
        /// </summary>
        private readonly SampleAccumulator rxAccumulator;

        /// <summary>
        /// The sample rate that the TX callback expects
        /// </summary>
        private readonly int txOutputSampleRate;

        /// <summary>
        /// Whether this audio bridge is RX only (no TX audio to handle)
        /// </summary>
        private readonly bool rxOnly;

        /// <summary>
        /// Sequence tracker for outgoing audio messages
        /// </summary>
        private uint rxSequence = 0;

        /// <summary>
        /// Sequence tracker for incoming audio messages
        /// </summary>
        private uint? txSequence = null;

        /// <summary>
        /// RX resampling filter
        /// </summary>
        private BiQuadFilter? rxResamplingLowPassFilter;
        /// <summary>
        /// TX resampling filter
        /// </summary>
        private BiQuadFilter? txResamplingLowPassFilter;

        /// <summary>
        /// This watchdog will fire if we abruptly stop receiving TX audio, and will gracefully 
        /// handle a reconnect or failure including dekeying the radio if keyed
        /// </summary>
        private readonly System.Timers.Timer txWatchdog = new System.Timers.Timer(500) { AutoReset = false };

        /// <summary>
        /// Set by RC2Server whenever RadioStatus.State changes, so the watchdog
        /// only fires while a transmission is actually expected.
        /// </summary>
        public bool TxActive { get; set; } = false;

        /// <summary>
        /// The configured RX sample rate
        /// </summary>
        public int RxSampleRateHz => rxFrameConfig.SampleRateHz;
        /// <summary>
        /// The configured TX sample rate
        /// </summary>
        public int? TxSampleRateHz => txFrameConfig?.SampleRateHz;

        /// <summary>
        /// Action fired when the TX watchdog (above) is tripped
        /// </summary>
        public event Action? OnTxWatchdogTripped;
        /// <summary>
        /// Action fired when an encoded RX frame is ready to send
        /// </summary>
        public event Action<AudioFrame>? OnEncodedRxFrame;   // -> RC2Server.SendAudioFrame
        /// <summary>
        /// Callback to handle incoming TX audio
        /// </summary>
        public event Action<short[], int>? TxAudioCallback;  // decoded mic PCM -> Radio hardware interface (unchanged signature)

        /// <summary>
        /// Instantiates a new AudioBridge
        /// </summary>
        /// <param name="rxSampleRate">the samplerate for incoming RX (speaker) audio from the hardware device</param>
        /// <param name="txSampleRate">the sameplrate for incoming TX (mic) audio from the console connection(</param>
        /// <param name="txOutputSampleRate">the samplerate that the TX (mic) audio device expects for outgoing TX audio</param>
        /// <param name="rxOnly">whether this audio bridge should only handle outgoing (receive) audio</param>
        public AudioBridge(int rxSampleRate, int txSampleRate, int txOutputSampleRate, bool rxOnly)
        {
            // Store samplerates
            this.txOutputSampleRate = txOutputSampleRate;
            this.rxOnly = rxOnly;

            // Create OPUS receive objects
            rxFrameConfig = new OpusFrameConfig(rxSampleRate);
            rxEncoder = OpusCodecFactory.CreateEncoder(rxFrameConfig.SampleRateHz, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            rxAccumulator = new SampleAccumulator(rxFrameConfig.FrameSampleLength);

            if (!rxOnly)
            {
                // Create OPUS transmit objects
                txFrameConfig = new OpusFrameConfig(txSampleRate);
                txDecoder = OpusCodecFactory.CreateDecoder(txFrameConfig.SampleRateHz, 1);

                // Set up TX samples watchdog
                txWatchdog.Elapsed += (s, e) =>
                {
                    if (TxActive)
                    {
                        Log.Logger.Error("No MIC audio for 500ms during active TX");
                        OnTxWatchdogTripped?.Invoke();
                    }
                };
            }
        }

        /// <summary>
        /// Encode and emit PCM16 RX samples as an AudioFrame
        /// </summary>
        public void SendRxSamples(short[] pcm16Samples, uint pcmSampleRate)
        {
            // Copy the samples from the input
            short[] toEncode = pcm16Samples;

            // Resample if needed
            if (pcmSampleRate != (uint)rxFrameConfig.SampleRateHz)
            {
                // Resample
                toEncode = AudioResampler.ResamplePcm16(pcm16Samples, (int)pcmSampleRate, rxFrameConfig.SampleRateHz);
                // Filter
                rxResamplingLowPassFilter ??= BiQuadFilter.LowPassFilter(rxFrameConfig.SampleRateHz, (float)(pcmSampleRate * 0.95 / 2.0), 4);
                for (int i = 0; i < toEncode.Length; i++)
                {
                    float sample = toEncode[i] / (float)short.MaxValue;
                    toEncode[i] = (short)(rxResamplingLowPassFilter.Transform(sample) * short.MaxValue);
                }
            }

            // Push to RX accumulator and send if accumulator is filled
            rxAccumulator.Push(toEncode, frame =>
            {
                // Prepare the buffer, sized to handle largest possible OPUS frame
                byte[] encoded = new byte[4000];
                // Encode and store actual size of encoded bytes
                int len = rxEncoder.Encode(frame, frame.Length, encoded, encoded.Length);
                // Send the frame
                OnEncodedRxFrame?.Invoke(new AudioFrame
                {
                    Source = AudioSource.Speaker,
                    Codec = AudioCodec.Opus,
                    Sequence = rxSequence++,
                    SampleRateHz = (uint)rxFrameConfig.SampleRateHz,
                    Channels = 1,
                    Data = ByteString.CopyFrom(encoded, 0, len)
                });
            });
        }

        /// <summary>
        /// Handle an incoming TX (speaker) audio frame
        /// </summary>
        /// <param name="frame">the incoming TX audio frame</param>
        /// <exception cref="ArgumentException">thrown if the frame can't be resampled to the required output rate</exception>
        public void HandleTxFrame(AudioFrame frame)
        {
            // Do nothing with these samples if we're configured for RX only
            if (rxOnly) return;

            // If for some reason txDecoder is null, catch it here
            if (txDecoder == null || txFrameConfig == null)
            {
                Log.Logger.Warning("TX Decoder not initialized, cannot handle TX audio");
                return;
            }

            // Validate samplerate
            if (frame.SampleRateHz != (uint)txFrameConfig.SampleRateHz)
            {
                Log.Logger.Error("TX audio sample rate {txrate} doesn't match configured output rate {outputrate}, dropping frame", frame.SampleRateHz, txFrameConfig.SampleRateHz);
                return;
            }

            // Check for dropped frames
            if (txSequence.HasValue && frame.Sequence != txSequence.Value + 1)
            {
                Log.Logger.Warning("Missed {missed} TX audio frames", frame.Sequence - txSequence.Value - 1);
            }
            txSequence = frame.Sequence;

            // Reset the TX sample watchdog timer
            txWatchdog.Stop();
            txWatchdog.Start();

            // Prepare decoded sample buffer
            short[] pcm16 = new short[txFrameConfig.FrameSampleLength];
            int decoded = txDecoder.Decode(frame.Data.Span, pcm16, txFrameConfig.FrameSampleLength, false);

            // Resample if required
            if (txOutputSampleRate < txFrameConfig.SampleRateHz)
            {
                // Filter first
                txResamplingLowPassFilter ??= BiQuadFilter.LowPassFilter(txFrameConfig.SampleRateHz, (float)(txOutputSampleRate * 0.95 / 2.0), 4);
                short[] filtered = new short[decoded];
                for (int i = 0; i < decoded; i++)
                {
                    float sample = pcm16[i] / (float)short.MaxValue;
                    filtered[i] = (short)(txResamplingLowPassFilter.Transform(sample) * short.MaxValue);
                }
                // Resample
                short[] resampled = AudioResampler.ResamplePcm16(filtered, txFrameConfig.SampleRateHz, txOutputSampleRate);
                // Send to the callback
                TxAudioCallback?.Invoke(resampled, txOutputSampleRate);
            }
            else if (txOutputSampleRate > txFrameConfig.SampleRateHz)
            {
                throw new ArgumentException("Resampling TX samples to higher sample rate not yet supported!");
            }
            else
            {
                TxAudioCallback?.Invoke(pcm16[..decoded], txFrameConfig.SampleRateHz);
            }
        }
    }
}