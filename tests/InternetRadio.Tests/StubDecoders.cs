using System;
using InternetRadio;

namespace InternetRadio.Tests
{
    /// <summary>
    /// No-op decoder for registry checks: it consumes bytes and emits nothing.
    /// </summary>
    internal sealed class StubDecoder : IAudioDecoder
    {
        public event Action<PcmFrame> PcmDecoded;

        /// <summary>Total bytes fed to this instance.</summary>
        public int Fed { get; private set; }

        public void Feed(byte[] data, int offset, int count)
        {
            Fed += count;
        }

        /// <summary>Emits a frame, so the event is a real part of the stub's contract.</summary>
        public void Emit(PcmFrame frame) => PcmDecoded?.Invoke(frame);

        public void Reset() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Configurable factory used to verify registry resolution rules without any
    /// real codec or audio file.
    /// </summary>
    internal sealed class StubDecoderFactory : IAudioDecoderFactory
    {
        private readonly string _contentMatch;
        private readonly byte[] _magic;

        public StubDecoderFactory(string name, string contentMatch = null, byte[] magic = null,
            bool usesIcyMetadata = true)
        {
            Name = name;
            _contentMatch = contentMatch;
            _magic = magic;
            UsesIcyMetadata = usesIcyMetadata;
        }

        public string Name { get; }

        public bool UsesIcyMetadata { get; }

        public StubDecoder LastDecoder { get; private set; }

        public bool CanDecodeContentType(string contentType)
        {
            if (string.IsNullOrEmpty(_contentMatch) || string.IsNullOrEmpty(contentType))
                return false;
            return contentType.IndexOf(_contentMatch, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool CanDecodeMagic(byte[] sniff, int sniffLength)
        {
            if (_magic == null || sniff == null || sniffLength < _magic.Length)
                return false;

            for (int i = 0; i < _magic.Length; i++)
            {
                if (sniff[i] != _magic[i])
                    return false;
            }

            return true;
        }

        public IAudioDecoder Create()
        {
            LastDecoder = new StubDecoder();
            return LastDecoder;
        }
    }
}
