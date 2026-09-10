using System;
using System.Collections.Generic;

namespace InternetRadio
{
    /// <summary>
    /// Ordered set of decoder factories. Detection runs in two passes over the
    /// registration order: first by Content-Type, then by byte signature, so a
    /// declared media type always wins over sniffing.
    ///
    /// The default registry contains MP3 only. Register an extra factory to add a
    /// codec:
    /// <code>
    /// radio.Decoders.Register(new OggVorbisDecoderFactory());
    /// </code>
    /// </summary>
    public sealed class AudioDecoderRegistry
    {
        private readonly List<IAudioDecoderFactory> _factories = new List<IAudioDecoderFactory>();

        /// <summary>Registry with the built-in codecs (MP3).</summary>
        public static AudioDecoderRegistry CreateDefault()
        {
            var registry = new AudioDecoderRegistry();
            registry.Register(new Mp3DecoderFactory());
            return registry;
        }

        /// <summary>Registered factories in detection order.</summary>
        public IReadOnlyList<IAudioDecoderFactory> Factories => _factories;

        public int Count => _factories.Count;

        /// <summary>Comma-separated factory names, for diagnostics.</summary>
        public string Names
        {
            get
            {
                if (_factories.Count == 0)
                    return "(none)";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < _factories.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(_factories[i].Name);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Adds a factory to the end of the detection order.
        /// </summary>
        /// <param name="factory">Factory to add.</param>
        /// <param name="replaceExisting">
        /// When true, an already registered factory with the same <see cref="IAudioDecoderFactory.Name"/>
        /// is replaced in place (keeping its detection position); otherwise the
        /// duplicate name throws.
        /// </param>
        public void Register(IAudioDecoderFactory factory, bool replaceExisting = false)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (string.IsNullOrEmpty(factory.Name))
                throw new ArgumentException("The factory must expose a non-empty Name.", nameof(factory));

            for (int i = 0; i < _factories.Count; i++)
            {
                if (!string.Equals(_factories[i].Name, factory.Name, StringComparison.Ordinal))
                    continue;

                if (!replaceExisting)
                    throw new InvalidOperationException("A decoder factory named '" + factory.Name + "' is already registered.");

                _factories[i] = factory;
                return;
            }

            _factories.Add(factory);
        }

        /// <summary>Removes a factory by name. Returns false when it was not registered.</summary>
        public bool Unregister(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i < _factories.Count; i++)
            {
                if (!string.Equals(_factories[i].Name, name, StringComparison.Ordinal))
                    continue;

                _factories.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Finds the first factory able to decode the described stream, or null.</summary>
        public IAudioDecoderFactory Resolve(string contentType, byte[] sniff, int sniffLength)
        {
            // Pass 1: an explicitly declared media type wins.
            if (!string.IsNullOrEmpty(contentType))
            {
                for (int i = 0; i < _factories.Count; i++)
                {
                    if (_factories[i].CanDecodeContentType(contentType))
                        return _factories[i];
                }
            }

            // Pass 2: byte signature.
            if (sniff != null && sniffLength > 0)
            {
                for (int i = 0; i < _factories.Count; i++)
                {
                    if (_factories[i].CanDecodeMagic(sniff, sniffLength))
                        return _factories[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a decoder for the described stream.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// No registered factory accepts the stream. The message lists the registered
        /// codecs so a missing plugin is obvious.
        /// </exception>
        public IAudioDecoder Create(string contentType, byte[] sniff, int sniffLength)
        {
            IAudioDecoderFactory factory = Resolve(contentType, sniff, sniffLength);
            if (factory == null)
            {
                throw new NotSupportedException(
                    "No registered decoder accepts this stream. Registered codecs: " + Names +
                    ". Content-Type: " + (string.IsNullOrEmpty(contentType) ? "(none)" : contentType) +
                    ", leading bytes: " + DescribeMagic(sniff, sniffLength) + ".");
            }

            return factory.Create();
        }

        /// <summary>Hex preview of the sniffed bytes (up to 8), for error messages.</summary>
        internal static string DescribeMagic(byte[] sniff, int sniffLength)
        {
            if (sniff == null || sniffLength <= 0)
                return "(none)";

            int n = Math.Min(sniffLength, 8);
            var sb = new System.Text.StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(sniff[i].ToString("x2"));
            }
            if (sniffLength > n)
                sb.Append(" ...");
            return sb.ToString();
        }
    }
}
