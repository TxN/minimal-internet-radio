using System;

namespace InternetRadio.OggVorbis
{
    /// <summary>
    /// Registers Ogg Vorbis decoding with the player:
    /// <code>
    /// radio.Decoders.Register(new OggVorbisDecoderFactory());
    /// </code>
    ///
    /// Detection is signature-based: the factory claims a stream when an Ogg page carries
    /// the Vorbis identification header (0x01 "vorbis") as its first packet. A generic
    /// <c>application/ogg</c> Content-Type alone is NOT enough, so a non-Vorbis Ogg stream
    /// (Opus, FLAC) is left for the decoder that actually handles it instead of being
    /// claimed here and failing later.
    /// </summary>
    public sealed class OggVorbisDecoderFactory : IAudioDecoderFactory
    {
        /// <summary>Payload of the Vorbis identification header packet: 0x01 "vorbis".</summary>
        private static readonly byte[] VorbisIdPrefix =
        {
            0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'
        };

        public string Name => "ogg-vorbis";

        /// <summary>
        /// Ogg has its own framing: in-band ICY metadata (<c>icy-metaint</c>) must never be
        /// stripped from it, because that would cut bytes out of the middle of a page.
        /// </summary>
        public bool UsesIcyMetadata => false;

        public bool CanDecodeContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
                return false;

            string ct = contentType.Trim().ToLowerInvariant();
            if (ct.IndexOf("vorbis", StringComparison.Ordinal) >= 0)
                return true;

            // Some servers label Vorbis streams as "audio/ogg" or "application/ogg".
            // The container alone does not identify the codec, but combined with the
            // signature check in CanDecodeMagic it does; the registry tries this factory
            // again in the magic pass, so claiming the container here is unnecessary.
            return false;
        }

        public bool CanDecodeMagic(byte[] sniff, int sniffLength)
        {
            return LooksLikeVorbisStream(sniff, sniffLength);
        }

        public IAudioDecoder Create()
        {
            return new OggVorbisDecoder();
        }

        /// <summary>
        /// True when the sniffed bytes start with an Ogg page whose first packet is the
        /// Vorbis identification header. Returns false while the window is too small to
        /// tell (the pipeline keeps sniffing up to its detection window).
        /// </summary>
        internal static bool LooksLikeVorbisStream(byte[] sniff, int sniffLength)
        {
            if (sniff == null || sniffLength < 28)
                return false;

            // Ogg page header: "OggS" + version + type + granule(8) + serial(4) + seq(4)
            // + crc(4) = 27 bytes, then the segment table (page_segments entries).
            if (sniff[0] != (byte)'O' || sniff[1] != (byte)'g' ||
                sniff[2] != (byte)'g' || sniff[3] != (byte)'S')
                return false;

            if (sniff[4] != 0)
                return false; // unknown stream structure version

            int segments = sniff[26];
            int payload = 27 + segments;
            if (payload + VorbisIdPrefix.Length > sniffLength)
                return false;

            for (int i = 0; i < VorbisIdPrefix.Length; i++)
            {
                if (sniff[payload + i] != VorbisIdPrefix[i])
                    return false;
            }

            return true;
        }
    }
}
