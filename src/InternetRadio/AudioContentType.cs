using System;
using System.IO;

namespace InternetRadio
{
    /// <summary>
    /// Content-Type hints for local files, where no HTTP headers exist. A hint is what lets
    /// codec detection succeed when the signature alone cannot decide (for example an MP3 whose
    /// ID3v2 tag is larger than the detection window).
    /// </summary>
    internal static class AudioContentType
    {
        public static string FromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".ogg":
                case ".oga":
                case ".opus": return "application/ogg";
                case ".aac": return "audio/aac";
                default: return null;
            }
        }
    }
}
