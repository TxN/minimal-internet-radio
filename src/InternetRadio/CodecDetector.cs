using System;

namespace InternetRadio
{
    internal enum StreamCodec
    {
        Unknown,
        Mp3,
        Aac,
        Ogg
    }

    /// <summary>Detects the stream codec from the Content-Type header or byte signature.</summary>
    internal static class CodecDetector
    {
        public static StreamCodec Detect(string contentType, byte[] sniff)
        {
            StreamCodec c = FromContentType(contentType);
            if (c != StreamCodec.Unknown)
                return c;
            return FromMagic(sniff);
        }

        public static StreamCodec FromContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
                return StreamCodec.Unknown;

            string ct = contentType.Trim().ToLowerInvariant();
            if (ct.IndexOf("mpeg", StringComparison.Ordinal) >= 0 ||
                ct.IndexOf("mp3", StringComparison.Ordinal) >= 0)
                return StreamCodec.Mp3;
            if (ct.IndexOf("aac", StringComparison.Ordinal) >= 0)
                return StreamCodec.Aac;
            if (ct.IndexOf("ogg", StringComparison.Ordinal) >= 0 ||
                ct.IndexOf("vorbis", StringComparison.Ordinal) >= 0 ||
                ct.IndexOf("opus", StringComparison.Ordinal) >= 0)
                return StreamCodec.Ogg;
            return StreamCodec.Unknown;
        }

        public static StreamCodec FromMagic(byte[] b)
        {
            if (b == null || b.Length < 4)
                return StreamCodec.Unknown;

            // MPEG audio frame sync: 0xFF followed by 11 one-bits; layer must not be 0.
            if (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0 && (b[1] & 0x06) != 0x00)
                return StreamCodec.Mp3;

            // ADTS AAC: 12-bit sync 0xFFF.
            if (b[0] == 0xFF && (b[1] & 0xF6) == 0xF0)
                return StreamCodec.Aac;

            // Ogg container magic.
            if (b[0] == (byte)'O' && b[1] == (byte)'g' && b[2] == (byte)'g' && b[3] == (byte)'S')
                return StreamCodec.Ogg;

            return StreamCodec.Unknown;
        }
    }
}
