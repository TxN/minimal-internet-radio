namespace InternetRadio
{
    /// <summary>
    /// Factory for the built-in MPEG Layer III decoder. Registered by default in
    /// <see cref="AudioDecoderRegistry.CreateDefault"/> and serves as the reference
    /// example of an <see cref="IAudioDecoderFactory"/> implementation.
    /// </summary>
    public sealed class Mp3DecoderFactory : IAudioDecoderFactory
    {
        public string Name => "mp3";

        /// <summary>MP3 streams are Shoutcast/Icecast framed: <c>icy-metaint</c> applies.</summary>
        public bool UsesIcyMetadata => true;

        public bool CanDecodeContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
                return false;

            string ct = contentType.Trim().ToLowerInvariant();
            return ct.IndexOf("mpeg", System.StringComparison.Ordinal) >= 0 ||
                   ct.IndexOf("mp3", System.StringComparison.Ordinal) >= 0;
        }

        public bool CanDecodeMagic(byte[] sniff, int sniffLength)
        {
            if (sniff == null)
                return false;

            int offset = SkipId3v2Tag(sniff, sniffLength);
            if (offset < 0 || offset + 2 > sniffLength)
                return false;

            // MPEG audio frame sync: 0xFF followed by 11 one-bits; layer must not be 0.
            return sniff[offset] == 0xFF && (sniff[offset + 1] & 0xE0) == 0xE0 &&
                   (sniff[offset + 1] & 0x06) != 0x00;
        }

        /// <summary>
        /// Offset of the first MPEG frame after a leading ID3v2 tag, or 0 when there is no
        /// tag. Returns -1 when a tag is present but runs past the sniffed window, so the
        /// signature cannot be confirmed from these bytes.
        /// </summary>
        private static int SkipId3v2Tag(byte[] sniff, int sniffLength)
        {
            // "ID3" + major + revision + flags + 4-byte syncsafe size.
            if (sniffLength < 10 || sniff[0] != (byte)'I' || sniff[1] != (byte)'D' || sniff[2] != (byte)'3')
                return 0;

            int size = ((sniff[6] & 0x7F) << 21) | ((sniff[7] & 0x7F) << 14) |
                       ((sniff[8] & 0x7F) << 7) | (sniff[9] & 0x7F);
            int offset = 10 + size;
            if ((sniff[5] & 0x10) != 0)
                offset += 10; // footer present

            return offset < sniffLength ? offset : -1;
        }

        public IAudioDecoder Create()
        {
            return new Mp3Decoder();
        }
    }
}
