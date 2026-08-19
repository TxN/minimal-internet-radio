namespace InternetRadio
{
    /// <summary>Metadata about the connected radio station.</summary>
    public sealed class StationInfo
    {
        /// <summary>icy-name header, when present.</summary>
        public string Name { get; set; }

        /// <summary>icy-genre header, when present.</summary>
        public string Genre { get; set; }

        /// <summary>icy-url header, when present.</summary>
        public string Url { get; set; }

        /// <summary>Declared bitrate in kbps (icy-br), or 0 if unknown.</summary>
        public int BitrateKbps { get; set; }

        /// <summary>Declared sample rate in Hz (icy-sr), or 0 if unknown.</summary>
        public int SampleRate { get; set; }

        /// <summary>Media type of the stream (Content-Type).</summary>
        public string ContentType { get; set; }

        /// <summary>In-stream metadata interval in bytes (icy-metaint), 0 = none.</summary>
        public int MetadataInterval { get; set; }
    }
}
