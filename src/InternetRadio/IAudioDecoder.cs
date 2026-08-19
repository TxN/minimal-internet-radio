using System;

namespace InternetRadio
{
    /// <summary>
    /// Streaming audio decoder. Implementations consume compressed bytes and
    /// emit decoded PCM through <see cref="PcmDecoded"/>. Implementations must be
    /// able to resynchronize on an arbitrary byte boundary (streams are split by
    /// in-band metadata and may reconnect).
    /// </summary>
    public interface IAudioDecoder : IDisposable
    {
        /// <summary>Raised for every decoded frame of PCM (interleaved 16-bit).</summary>
        event Action<PcmFrame> PcmDecoded;

        /// <summary>Feed compressed audio bytes. May raise <see cref="PcmDecoded"/>.</summary>
        void Feed(byte[] data, int offset, int count);

        /// <summary>Drop all state and resynchronize on the next input.</summary>
        void Reset();
    }
}
