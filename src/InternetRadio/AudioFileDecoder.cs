using System;
using System.IO;
using System.Threading;

namespace InternetRadio
{
    /// <summary>
    /// Decodes a compressed audio source — a file or any forward-only stream — to PCM in
    /// streaming mode: the source is read in bounded chunks and pushed to a decoder taken from
    /// <see cref="Decoders"/>, so memory use does not depend on the source length.
    ///
    /// Background use, with a completion notification:
    /// <code>
    /// var decoder = new AudioFileDecoder();
    /// decoder.SetSource("music.ogg");
    /// decoder.PcmDecoded += frame => { /* frame.Samples, frame.SampleRate, frame.Channels */ };
    /// decoder.Completed  += () => Console.WriteLine("decoded");
    /// decoder.Error      += e => Console.WriteLine(e.Message);
    /// decoder.Start();
    /// // ...
    /// decoder.Stop();   // Stop cancels: Completed is not raised
    /// </code>
    ///
    /// Synchronous use:
    /// <code>
    /// long samples = AudioFileDecoder.DecodeAll("music.ogg", onFrame: frame => { ... });
    /// </code>
    ///
    /// Events are raised on the worker thread; marshal to the main/UI thread yourself. The
    /// frame's sample array is reused between frames (see <see cref="PcmFrame"/>), so copy it
    /// inside the handler if it must outlive the call.
    /// </summary>
    public sealed class AudioFileDecoder : IDisposable
    {
        private const int DefaultReadBufferBytes = 64 * 1024;
        private const int DefaultDetectionBytes = 1024;

        /// <summary>
        /// Upper bound for extending detection past a leading ID3v2 tag: a file whose tag is
        /// larger than this needs an explicit <c>contentTypeHint</c>.
        /// </summary>
        private const int MaxDetectionBytes = 1024 * 1024;

        private readonly object _sync = new object();
        private readonly AudioDecoderRegistry _decoders;

        private Stream _source;
        private bool _ownsSource;
        private string _contentTypeHint;
        private Thread _thread;
        private volatile bool _stopRequested;
        private long _bytesRead;
        private long _samplesDecoded;

        public AudioFileDecoder(AudioDecoderRegistry registry = null)
        {
            _decoders = registry ?? AudioDecoderRegistry.CreateDefault();
        }

        /// <summary>Decoders available to this instance (the built-in codecs by default).</summary>
        public AudioDecoderRegistry Decoders => _decoders;

        /// <summary>Size of the read buffer used while streaming the source.</summary>
        public int ReadBufferBytes { get; set; } = DefaultReadBufferBytes;

        /// <summary>
        /// Leading bytes read before a codec is chosen. The window is extended automatically
        /// past a leading ID3v2 tag, so tagged MP3 files decode without a hint.
        /// </summary>
        public int DetectionBytes { get; set; } = DefaultDetectionBytes;

        /// <summary>True between <see cref="Start"/> and the end of the background decode.</summary>
        public bool IsRunning => _thread != null;

        /// <summary>Source bytes read so far.</summary>
        public long BytesRead => Interlocked.Read(ref _bytesRead);

        /// <summary>Interleaved samples emitted so far.</summary>
        public long SamplesDecoded => Interlocked.Read(ref _samplesDecoded);

        /// <summary>Raised for every decoded frame. The frame's array is reused between calls.</summary>
        public event Action<PcmFrame> PcmDecoded;

        /// <summary>Raised once, when the whole source has been read and decoded.</summary>
        public event Action Completed;

        /// <summary>Raised on read/decode errors. <see cref="Completed"/> is not raised after an error.</summary>
        public event Action<Exception> Error;

        /// <summary>
        /// Raised when the decoder reports container tags (for Ogg Vorbis — the Vorbis comment).
        /// A chained stream reports them again for every logical stream, so the last values are
        /// the tags of the track being decoded.
        /// </summary>
        public event Action<AudioTags> TagsChanged;

        /// <summary>Last tags reported by the decoder, or an empty value when there were none.</summary>
        public AudioTags Tags { get; private set; }

        /// <summary>Sets a file as the source. The content type is derived from the extension.</summary>
        public void SetSource(string path, string contentTypeHint = null)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Source path is empty.", nameof(path));

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            SetSource(stream, contentTypeHint ?? AudioContentType.FromPath(path), leaveOpen: false);
        }

        /// <summary>
        /// Sets any forward-only stream as the source (a file, a network stream, a memory stream).
        /// </summary>
        /// <param name="stream">Stream to read. Reading starts at its current position.</param>
        /// <param name="contentTypeHint">Optional hint, e.g. <c>audio/mpeg</c>; when null only the signature is used.</param>
        /// <param name="leaveOpen">True to keep the stream open after decoding finishes or on dispose.</param>
        public void SetSource(Stream stream, string contentTypeHint = null, bool leaveOpen = false)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            lock (_sync)
            {
                if (_thread != null)
                    throw new InvalidOperationException("Stop the decoder before changing the source.");

                _source = stream;
                _ownsSource = !leaveOpen;
                _contentTypeHint = contentTypeHint;
            }
        }

        /// <summary>Starts decoding on a background thread.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_thread != null)
                    return;
                if (_source == null)
                    throw new InvalidOperationException("Set a source first (SetSource).");

                _stopRequested = false;
                _bytesRead = 0;
                _samplesDecoded = 0;
                _thread = new Thread(RunOnThread)
                {
                    IsBackground = true,
                    Name = "InternetRadio.FileDecoder",
                    Priority = ThreadPriority.AboveNormal,
                };
                _thread.Start();
            }
        }

        /// <summary>
        /// Cancels the background decode and waits for it to stop (up to <paramref name="timeoutMs"/>).
        /// <see cref="Completed"/> is not raised for a cancelled decode.
        /// </summary>
        public void Stop(int timeoutMs = 5000)
        {
            Thread thread;
            lock (_sync)
            {
                _stopRequested = true;
                thread = _thread;
            }

            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(timeoutMs);
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Decodes a whole source synchronously, streaming (bounded memory), and returns the
        /// number of interleaved samples emitted. The caller keeps ownership of the stream.
        /// </summary>
        /// <param name="source">Stream to decode.</param>
        /// <param name="registry">Decoders to use; the built-in set when null.</param>
        /// <param name="contentTypeHint">Optional hint, e.g. <c>audio/mpeg</c>.</param>
        /// <param name="onFrame">Called for every decoded frame.</param>
        /// <param name="onTags">Called when the container reports tags (may be called more than once for a chained stream).</param>
        public static long DecodeAll(Stream source, AudioDecoderRegistry registry = null,
            string contentTypeHint = null, Action<PcmFrame> onFrame = null, Action<AudioTags> onTags = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var decoder = new AudioFileDecoder(registry);
            return decoder.DecodeSource(source, contentTypeHint, onFrame, null, onTags);
        }

        /// <summary>Decodes a whole file synchronously, streaming, and returns the sample count.</summary>
        public static long DecodeAll(string path, AudioDecoderRegistry registry = null,
            Action<PcmFrame> onFrame = null, Action<AudioTags> onTags = null)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Source path is empty.", nameof(path));

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       64 * 1024, FileOptions.SequentialScan))
            {
                return DecodeAll(stream, registry, AudioContentType.FromPath(path), onFrame, onTags);
            }
        }

        private void RunOnThread()
        {
            Stream source;
            bool ownsSource;
            string hint;

            lock (_sync)
            {
                source = _source;
                ownsSource = _ownsSource;
                hint = _contentTypeHint;
            }

            bool failed = false;
            try
            {
                DecodeSource(source, hint, RaiseFrame, () => _stopRequested);
            }
            catch (Exception ex)
            {
                failed = true;
                Error?.Invoke(ex);
            }
            finally
            {
                if (ownsSource)
                {
                    try { source.Dispose(); }
                    catch { /* ignore */ }
                }

                lock (_sync)
                {
                    _source = null;
                    _ownsSource = false;
                    _thread = null;
                }

                if (!failed && !_stopRequested)
                    Completed?.Invoke();
            }
        }

        /// <summary>
        /// Reads the source, resolves a decoder from the leading bytes and decodes everything.
        /// Only the read buffer plus a bounded detection window are held at any time.
        /// </summary>
        private long DecodeSource(Stream source, string contentTypeHint, Action<PcmFrame> onFrame,
            Func<bool> isCancelled, Action<AudioTags> onTags = null)
        {
            int detectionBytes = DetectionBytes;
            if (detectionBytes < 16)
                detectionBytes = 16;
            if (detectionBytes > MaxDetectionBytes)
                detectionBytes = MaxDetectionBytes;

            int window = detectionBytes;
            byte[] sniff = new byte[window];
            int sniffLength = 0;

            while (true)
            {
                while (sniffLength < window)
                {
                    int read = source.Read(sniff, sniffLength, window - sniffLength);
                    if (read <= 0)
                        break;

                    Interlocked.Add(ref _bytesRead, read);
                    sniffLength += read;
                }

                // An MP3 with a large ID3v2 tag hides its frame sync behind the tag: widen the
                // window to cover it (bounded), instead of failing detection.
                int extended = Id3AwareWindow(sniff, sniffLength, window);
                if (extended > window && extended <= MaxDetectionBytes)
                {
                    Array.Resize(ref sniff, extended);
                    window = extended;
                    continue;
                }

                break;
            }

            IAudioDecoderFactory factory = _decoders.Resolve(contentTypeHint, sniff, sniffLength);
            if (factory == null)
            {
                throw new NotSupportedException(
                    "No registered decoder accepts this source. Registered codecs: " + _decoders.Names +
                    ". Content-Type hint: " + (string.IsNullOrEmpty(contentTypeHint) ? "(none)" : contentTypeHint) +
                    ", leading bytes: " + AudioDecoderRegistry.DescribeMagic(sniff, sniffLength) + ".");
            }

            IAudioDecoder decoder = factory.Create();
            long emitted = 0;

            // Every frame is counted here, so both the instance API (through RaiseFrame) and the
            // synchronous one report the same number of samples.
            Action<PcmFrame> handler = frame =>
            {
                emitted += frame.Samples.Length;
                if (onFrame != null)
                    onFrame(frame);
            };

            var tagSource = decoder as IAudioTagSource;
            Action<AudioTags> tagHandler = null;
            if (tagSource != null)
            {
                tagHandler = tags =>
                {
                    RaiseTags(tags);
                    if (onTags != null)
                        onTags(tags);
                };
                tagSource.TagsChanged += tagHandler;
            }

            try
            {
                decoder.PcmDecoded += handler;

                // A file is clean audio: in-band ICY framing is not present, so the bytes are
                // handed over as they are.
                if (sniffLength > 0)
                    decoder.Feed(sniff, 0, sniffLength);

                byte[] buffer = new byte[Math.Max(4096, ReadBufferBytes)];
                while (isCancelled == null || !isCancelled())
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    Interlocked.Add(ref _bytesRead, read);
                    decoder.Feed(buffer, 0, read);
                }
            }
            finally
            {
                if (tagSource != null)
                    tagSource.TagsChanged -= tagHandler;

                decoder.PcmDecoded -= handler;
                decoder.Dispose();
            }

            return emitted;
        }

        /// <summary>
        /// Returns the window size needed to see the first MPEG frame after a leading ID3v2 tag,
        /// or the current window when there is no tag / the tag is already covered.
        /// </summary>
        private static int Id3AwareWindow(byte[] sniff, int sniffLength, int currentWindow)
        {
            if (sniffLength < 10 || sniff[0] != (byte)'I' || sniff[1] != (byte)'D' || sniff[2] != (byte)'3')
                return currentWindow;

            int size = ((sniff[6] & 0x7F) << 21) | ((sniff[7] & 0x7F) << 14) |
                       ((sniff[8] & 0x7F) << 7) | (sniff[9] & 0x7F);
            int tagEnd = 10 + size;
            if ((sniff[5] & 0x10) != 0)
                tagEnd += 10; // footer present

            int needed = tagEnd + 4; // room for the 4-byte MPEG frame header
            return needed > currentWindow ? needed : currentWindow;
        }

        private void RaiseFrame(PcmFrame frame)
        {
            Interlocked.Add(ref _samplesDecoded, frame.Samples.Length);

            try
            {
                PcmDecoded?.Invoke(frame);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }

        private void RaiseTags(AudioTags tags)
        {
            if (tags.IsEmpty)
                return;

            Tags = tags;
            try
            {
                TagsChanged?.Invoke(tags);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }
    }
}
