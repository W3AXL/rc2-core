using NAudio.Dsp;
using NAudio.Wave;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace rc2_core
{
    public partial class WebRTCPeer : WebSocketSharp.Server.WebSocketBehavior
    {
        /// <summary>
        /// Callback used to send pre-encoded audio samples to the peer connection
        /// </summary>
        /// <param name="durationRtpUnits"></param>
        /// <param name="encodedSamples"></param>
        public void RxAudioCallback(uint durationRtpUnits, byte[] encodedSamples)
        {
            // If we don't have a peer connection, return
            if (pc == null || audioFmt.RtpClockRate == 0)
            {
                //Log.Logger.Debug($"Ignoring RX samples, WebRTC peer not connected");
                return;
            }

            // Send audio
            pc.SendAudio(durationRtpUnits, encodedSamples);

            //Log.Logger.Debug($"Sent {encodedSamples.Length} ({durationRtpUnits * 1000 / RxFormat.RtpClockRate} ms) {RxFormat.Codec.ToString()} samples to WebRTC peer");

            // Record audio if enabled
            if (Record && recRxWriter != null)
            {
                // Decode samples to pcm
                short[] pcmSamples = RecRxEncoder.DecodeAudio(encodedSamples, audioFmt);
                // Convert to float s16
                float[] s16Samples = new float[pcmSamples.Length];
                for (int n = 0; n < pcmSamples.Length; n++)
                {
                    s16Samples[n] = pcmSamples[n] / 32768f * recRxGain;
                }
                // Add to buffer
                recRxWriter.WriteSamples(s16Samples, 0, s16Samples.Length);
            }

            // Reset our sample watchdog timer
            rxSampleTimer.Stop();
            rxSampleTimer.Start();
        }

        /// <summary>
        /// Callback used to send PCM16 samples to the peer connection
        /// </summary>
        /// <param name="durationRtpUnits"></param>
        /// <param name="pcm16Samples"></param>
        public void RxAudioCallback16(short[] pcm16Samples, uint pcmSampleRate)
        {
            // If we don't have a peer connection, return
            if (pc == null || audioFmt.RtpClockRate == 0)
            {
                //Log.Logger.Debug($"Ignoring RX samples, WebRTC peer not connected");
                return;
            }

            // Resample if needed
            if (pcmSampleRate < audioFmt.ClockRate)
            {
                short[] resampled = RxEncoder.Resample(pcm16Samples, (int)pcmSampleRate, audioFmt.ClockRate);

                // Create a low pass filter if we haven't already
                if (rxResamplingLowPassFilter == null)
                {
                    rxResamplingLowPassFilter = BiQuadFilter.LowPassFilter(audioFmt.ClockRate, (float)(pcmSampleRate * 0.95 / 2.0), 4);
                }

                // Filter the resampled audio
                short[] filtered = new short[resampled.Length];
                for (int i = 0; i < resampled.Length; i++)
                {
                    float sample = (float)resampled[i] / (float)short.MaxValue;
                    float flt = rxResamplingLowPassFilter.Transform(sample);
                    filtered[i] = (short)(flt * short.MaxValue);
                }

                byte[] encodedSamples = RxEncoder.EncodeAudio(filtered, audioFmt);
                this.RxAudioCallback((uint)encodedSamples.Length, encodedSamples);
            }
            else if (pcmSampleRate > audioFmt.ClockRate)
            {
                throw new ArgumentException("Resampling RX audio to lower sample rate not yet supported!");
            }
            else
            {
                byte[] encodedSamples = RxEncoder.EncodeAudio(pcm16Samples, audioFmt);
                this.RxAudioCallback((uint)encodedSamples.Length, encodedSamples);
            }
        }

        /// <summary>
        /// Handler to process configuration of audio formats after they've been negotiated
        /// </summary>
        /// <param name="formats"></param>
        private void audioFormatsNegotiated(List<AudioFormat> formats)
        {
            // Get the format
            audioFmt = formats.Find(f => f.FormatName == Codec);
            // Set the source to use the format
            //RxSource.SetAudioSourceFormat(RxFormat);
            Serilog.Log.Logger.Debug("Negotiated RX audio format {AudioFormat} ({ClockRate}/{Chs})", audioFmt.FormatName, audioFmt.ClockRate, audioFmt.ChannelCount);
            // Set our wave and buffer writers to the proper sample rate
            recFormat = new WaveFormat(audioFmt.ClockRate, 16, 1);
            // Fire the callback
            if (RTCFormatCallback != null)
            {
                RTCFormatCallback(audioFmt);
            }
        }

        /// <summary>
        /// Handler to process incoming RTP packets from the WebRTC peer connection
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="mediaType"></param>
        /// <param name="rtpPkt"></param>
        private void rtpPacketHandler(IPEndPoint endpoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPkt)
        {
            try
            {
                // Detect changes in SSRC
                if (rtpPkt.Header.SyncSource != syncSource)
                {
                    Serilog.Log.Logger.Warning("Detected change in WebRTC Sync Source {old} => {new}", syncSource, rtpPkt.Header.SyncSource);
                    syncSource = rtpPkt.Header.SyncSource;
                }

                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    if (!RxOnly)
                    {
                        //TxCallback(rep, media, rtpPkt);
                        decodeTxAudio(rtpPkt.Payload);
                    }

                    // Save TX audio to file, if we're supposed to and the file is open
                    if (Record && recTxWriter != null)
                    {
                        // Get samples
                        byte[] samples = rtpPkt.Payload;
                        // Decode samples
                        short[] pcmSamples = RecTxEncoder.DecodeAudio(samples, audioFmt);
                        // Convert to float s16
                        float[] s16Samples = new float[pcmSamples.Length];
                        for (int n = 0; n < pcmSamples.Length; n++)
                        {
                            s16Samples[n] = pcmSamples[n] / 32768f * recTxGain;
                        }
                        // Add to buffer
                        recTxWriter.WriteSamples(s16Samples, 0, s16Samples.Length);
                    }

                    // Reset TX sample watchdog timer
                    txSampleTimer.Stop();
                    txSampleTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "Caught exception during OnRtpPacketReceived event");
                throw;
            }
        }

        /// <summary>
        /// Decode RTP audio into PCM16 samples
        /// </summary>
        /// <param name="encodedSamples"></param>
        /// <param name="pcm16Samples"></param>
        /// <param name="pcmSampleRate"></param>
        private void decodeTxAudio(byte[] encodedSamples)
        {
            // Decode
            short[] pcm16Samples = TxEncoder.DecodeAudio(encodedSamples, audioFmt);

            // Resample if needed
            if (txAudioSamplerate < audioFmt.ClockRate)
            {
                // Create a low pass filter if we haven't already
                if (txResamplingLowPassFilter == null)
                {
                    txResamplingLowPassFilter = BiQuadFilter.LowPassFilter(audioFmt.ClockRate, (float)(txAudioSamplerate * 0.95 / 2.0), 4);
                }

                // Filter the raw audio
                short[] filtered = new short[pcm16Samples.Length];
                for (int i = 0; i < pcm16Samples.Length; i++)
                {
                    float sample = (float)pcm16Samples[i] / (float)short.MaxValue;
                    float flt = txResamplingLowPassFilter.Transform(sample);
                    filtered[i] = (short)(flt * short.MaxValue);
                }

                short[] resampled = TxEncoder.Resample(filtered, audioFmt.ClockRate, txAudioSamplerate);

                TxCallback(resampled);
            }
            else if (txAudioSamplerate > audioFmt.ClockRate)
            {
                throw new ArgumentException("Resampling TX samples to higher sample rate not yet supported!");
            }
            else
            {
                TxCallback(pcm16Samples);
            }
        }

        /// <summary>
        /// Start a wave recording with the specified file prefix
        /// </summary>
        /// <param name="prefix">filename prefix, appended with timestamp</param>
        public void RecStartTx(string name)
        {
            // Stop recording RX
            if (RecRxInProgress)
            {
                RecStop();
            }
            // Only create a new file if recording is enabled and we're not already recording TX
            if (Record && !RecTxInProgress)
            {
                // Get full filepath
                string filename = $"{RecPath}/{DateTime.Now.ToString(RecTsFmt)}_{name.Replace(' ', '_')}_TX.wav";
                // Create writer
                recTxWriter = new WaveFileWriter(filename, recFormat);
                Serilog.Log.Logger.Debug("Starting new TX recording: {file}", filename);
                // Set Flag
                RecTxInProgress = true;
            }
        }

        public void RecStartRx(string name)
        {
            // Stop recording TX
            if (RecTxInProgress)
            {
                RecStop();
            }
            // Only create a new file if recording is enabled and we're not already recording RX
            if (Record && !RecRxInProgress)
            {
                // Get full filepath
                string filename = $"{RecPath}/{DateTime.Now.ToString(RecTsFmt)}_{name.Replace(' ', '_')}_RX.wav";
                // Create writer
                recRxWriter = new WaveFileWriter(filename, recFormat);
                Serilog.Log.Logger.Debug("Starting new RX recording: {file}", filename);
                // Set Flag
                RecRxInProgress = true;
            }
        }

        /// <summary>
        /// Stop a wave recording
        /// </summary>
        public void RecStop()
        {
            if (recTxWriter != null)
            {
                recTxWriter.Close();
                recTxWriter = null;
            }
            if (recRxWriter != null)
            {
                recRxWriter.Close();
                recRxWriter = null;
            }
            RecTxInProgress = false;
            RecRxInProgress = false;
            Serilog.Log.Logger.Debug("Stopped recording");
        }

        public void SetRecGains(double rxGainDb, double txGainDb)
        {
            recRxGain = (float)Math.Pow(10, rxGainDb / 20);
            recTxGain = (float)Math.Pow(10, txGainDb / 20);
        }

        private void missingTxSampleCallback(object sender, ElapsedEventArgs e)
        {
            Serilog.Log.Logger.Error("WebRTC connection had no TX audio for 500ms!");
            _ = RestartPeerConnection("WebRTC connection had no TX audio for 500ms!");
        }

        private void missingRxSampleCallback(object sender, ElapsedEventArgs e)
        {
            //Log.Logger.Error("WebRTC connection had no RX audio for 500ms!");
            //throw new TimeoutException("WebRTC connection had no RX audio for 500ms!");
        }
    }
}
