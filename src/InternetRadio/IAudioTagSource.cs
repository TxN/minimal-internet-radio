using System;

namespace InternetRadio
{
    /// <summary>
    /// Optional capability of an <see cref="IAudioDecoder"/>: reporting container tags
    /// (Vorbis comment, ID3, …). The player subscribes when the decoder supports it and fills
    /// the empty fields of <see cref="StationInfo"/>.
    /// </summary>
    public interface IAudioTagSource
    {
        /// <summary>
        /// Raised when tags become known (usually once per stream, right after the headers were
        /// parsed) or change. Raised on the decoder's thread.
        /// </summary>
        event Action<AudioTags> TagsChanged;
    }
}
