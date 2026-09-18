using System;
using NAudio.Dmo;
using NAudio.Utils;

namespace rc2_core
{
    public class AudioResampler
    {
        /// <summary>
        /// Resample a PCM16 sample array
        /// </summary>
        /// <param name="inputSamples">the input sample array</param>
        /// <param name="inputSampleRate">the input samplerate</param>
        /// <param name="outputSampleRate">the desired output samplerate</param>
        /// <returns>the resampled audio array</returns>
        public static short[] ResamplePcm16(short[] inputSamples, int inputSampleRate, int outputSampleRate)
        {
            // Return samples 1:1 if we don't have to resample
            if (inputSampleRate == outputSampleRate) return inputSamples;

            // Calculate resampling ratio
            double sampleRateRatio = (double)outputSampleRate / (double)inputSampleRate;
            // Calculte the number of output samples from this ratio
            int outputSize = (int)(inputSamples.Length * sampleRateRatio);
            // Prepare the output buffer
            short[] outputBuffer = new short[outputSize];

            // Iterate over the output buffer
            for (int i = 0; i < outputSize; i++)
            {
                // Calculate the required parameters for the linear interpolation
                double inputPosition = i / sampleRateRatio;
                int leftIndex = (int)Math.Floor(inputPosition);
                int rightIndex = leftIndex + 1;
                double fraction = inputPosition - leftIndex;

                // If we're at the end of the input buffer, copy samples directly
                if (rightIndex >= inputSamples.Length)
                {
                    outputBuffer[i] = inputSamples[leftIndex];
                }
                // Otherwise, perform linear interpolation
                else
                {
                    // Basic interpolation between two points
                    double interpolatedSample = ( inputSamples[leftIndex] * (1 - fraction) ) + ( inputSamples[rightIndex] * fraction );
                    // Clamp to min/max
                    outputBuffer[i] = (short)Math.Clamp(interpolatedSample, short.MinValue, short.MaxValue);
                }
            }

            // Return the resampled array
            return outputBuffer;
        }
    }
}