namespace InternetRadio
{
    /// <summary>
    /// Identifies and creates a streaming audio decoder. Register factories on
    /// <see cref="AudioDecoderRegistry"/> to teach the player additional codecs
    /// (for example Ogg Vorbis) without modifying the core library.
    ///
    /// Implementations must be self-contained: they may only use the values passed
    /// to the detection methods, never internal state of this assembly. That keeps
    /// a third-party factory (in its own assembly) a first-class plugin.
    /// </summary>
    public interface IAudioDecoderFactory
    {
        /// <summary>Short identifier used in diagnostics, e.g. "mp3".</summary>
        string Name { get; }

        /// <summary>
        /// True when the response Content-Type denotes a stream this factory decodes.
        /// Checked before <see cref="CanDecodeMagic"/>: a declared media type wins over
        /// a byte signature.
        /// </summary>
        /// <param name="contentType">Content-Type header value, may be null or empty.</param>
        bool CanDecodeContentType(string contentType);

        /// <summary>
        /// True when the leading stream bytes carry this codec's signature. Used when
        /// the Content-Type is missing or unrecognized.
        /// </summary>
        /// <param name="sniff">Leading bytes of the stream body.</param>
        /// <param name="sniffLength">Number of valid bytes in <paramref name="sniff"/>.</param>
        bool CanDecodeMagic(byte[] sniff, int sniffLength);

        /// <summary>
        /// True when the stream carries Shoutcast/Icecast in-band metadata
        /// (<c>icy-metaint</c> framing) that must be stripped before decoding.
        /// Container formats with their own framing (Ogg) must return false:
        /// removing bytes from such a stream corrupts it.
        /// </summary>
        bool UsesIcyMetadata { get; }

        /// <summary>Creates a decoder instance. Called once per connection.</summary>
        IAudioDecoder Create();
    }
}
