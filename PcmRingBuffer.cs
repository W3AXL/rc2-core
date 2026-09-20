namespace rc2_core
{
    /// <summary>
    /// Ring buffer class for storing PCM16 audio samples
    /// </summary>
    public sealed class PcmRingBuffer
    {
        private readonly short[] buffer;
        private readonly object gate = new object();
        private int writeIdx = 0;
        private int readIdx = 0;
        private int available = 0;

        public PcmRingBuffer(int capacitySamples)
        {
            buffer = new short[capacitySamples];
        }

        /// <summary>
        /// Write samples to the buffer, dropping the oldest samples in it if we have an overflow
        /// </summary>
        /// <param name="samples"></param>
        public void Write(short[] samples)
        {
            lock (gate)
            {
                int n = samples.Length;
                if (available + n > buffer.Length)
                {
                    // Drop the OLDEST audio, not the newest
                    int overflow = available + n - buffer.Length;
                    readIdx = (readIdx + overflow) % buffer.Length;
                    available -= overflow;
                }
                for (int i = 0; i < n; i++)
                {
                    buffer[(writeIdx + i) % buffer.Length] = samples[i];
                }
                writeIdx = (writeIdx + n) % buffer.Length;
                available += n;
            }
        }

        /// <summary>
        /// Read audio from the buffer.
        /// 
        /// Dest[] will be filled completely, padded with silence for any shortfall in audio samples
        /// </summary>
        /// <param name="dest">the buffer to output the frames to</param>
        public void Read(short[] dest)
        {
            lock (gate)
            {
                int n = dest.Length;
                int canRead = Math.Min(available, n);

                for (int i = 0; i < canRead; i++)
                {
                    dest[i] = buffer[(readIdx + i) % buffer.Length];
                }
                if (canRead < n)
                {
                    Array.Clear(dest, canRead, n - canRead); // silence for the shortfall
                }

                readIdx = (readIdx + canRead) % buffer.Length;
                available -= canRead;
            }
        }
    }
}