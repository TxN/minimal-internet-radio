namespace InternetRadio
{
    /// <summary>
    /// A chunk of decoded, interleaved 16-bit PCM audio.
    /// For stereo: L,R,L,R,...  For mono: single channel samples.
    /// </summary>
    public readonly struct PcmFrame
    {
        /// <summary>
        /// Interleaved signed 16-bit samples. The array is reused between frames for
        /// zero-GC streaming: copy it during the <c>PcmDecoded</c> handler if you need
        /// to keep it past the next decoded frame.
        /// </summary>
        public readonly short[] Samples;

        /// <summary>Sample rate in Hz (e.g. 44100).</summary>
        public readonly int SampleRate;

        /// <summary>Channel count (1 = mono, 2 = stereo).</summary>
        public readonly int Channels;

        public PcmFrame(short[] samples, int sampleRate, int channels)
        {
            Samples = samples;
            SampleRate = sampleRate;
            Channels = channels;
        }
    }
}
