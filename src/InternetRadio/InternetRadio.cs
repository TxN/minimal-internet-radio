using System;
using System.IO;
using System.Text;
using System.Threading;

namespace InternetRadio
{
    /// <summary>
    /// Compact, dependency-free internet radio player.
    ///
    /// Pipeline: raw TCP/TLS stream reader -> thread-safe ring buffer ->
    /// ICY metadata splitter -> prebuffer -> registered decoder -> PCM events.
    /// Decoders are pluggable through <see cref="Decoders"/>; MP3 is built in.
    ///
    /// Usage:
    ///   var radio = new InternetRadio();
    ///   radio.PcmDecoded += frame => { /* copy frame.Samples to an AudioClip */ };
    ///   radio.StreamTitleChanged += title => Console.WriteLine(title);
    ///   radio.SetUrl("http://host:8000/stream");
    ///   radio.Start();
    ///   ...
    ///   radio.Stop();
    ///
    /// Additional codecs are opted into before Start, e.g.
    ///   radio.Decoders.Register(new OggVorbisDecoderFactory());
    ///
    /// All events are raised on background threads; marshal to the main/UI
    /// thread yourself if needed. No Unity or third-party dependencies.
    /// </summary>
    public sealed class InternetRadio : IDisposable
    {
        /// <summary>
        /// Leading stream bytes buffered for codec detection. Large enough to hold the
        /// identification header of container formats (an Ogg page header plus the Vorbis
        /// or Opus id packet) and to see past a leading ID3v2 tag of a typical size.
        /// Bytes consumed here are replayed to the decoder, so detection costs no audio.
        /// </summary>
        private const int SniffBytes = 1024;

        private readonly object _sync = new object();

        private string _url;
        private volatile int _state = (int)PlaybackState.Idle;
        private Thread _readerThread;
        private Thread _decoderThread;
        private RingBuffer _ring;

        // Published by the reader thread after every successful connect, read by the
        // decoder thread: volatile keeps the swap visible without locking.
        private volatile StationInfo _station;

        // Incremented by the reader thread for every new connection so the decoder
        // thread can tell a reconnect from a continuation of the same stream.
        private int _connectionEpoch;

        // Caches the last in-band metadata block so the (rare) title message is only
        // decoded/parsed when the raw block actually changes.
        private byte[] _lastMeta;
        private int _lastMetaLen;

        private volatile bool _stopRequested;
        private volatile bool _readerDone;
        private volatile bool _failed;

        /// <summary>Ring buffer capacity for decoupling the reader from the decoder.</summary>
        public int RingCapacityBytes { get; set; } = 1024 * 1024;

        /// <summary>Socket receive buffer (SO_RCVBUF) in bytes. A large value absorbs
        /// bursty server delivery. 0 keeps the OS default.</summary>
        public int SocketReceiveBufferBytes { get; set; } = 1024 * 1024;

        /// <summary>Clean audio bytes to accumulate before playback (decoding) starts.
        /// A 128 kbps stream is ~16 KB/s, so 128 KB is ~8 s of buffered audio.</summary>
        public int PrebufferBytes { get; set; } = 128 * 1024;

        /// <summary>TCP connect timeout.</summary>
        public int ConnectTimeoutMs { get; set; } = 10000;

        /// <summary>Socket read timeout; a silent stream longer than this triggers a reconnect.</summary>
        public int ReadTimeoutMs { get; set; } = 30000;

        /// <summary>Delay between reconnection attempts.</summary>
        public int ReconnectDelayMs { get; set; } = 1000;

        /// <summary>Automatically reconnect on connect/stream failure until Stop is called.</summary>
        public bool AutoReconnect { get; set; } = true;

        private long _bytesReceived;

        public PlaybackState State => (PlaybackState)_state;

        public string Url => _url;

        /// <summary>Total raw stream bytes received from the socket (audio + metadata).</summary>
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        /// <summary>Bytes currently buffered in the ring buffer awaiting decode.</summary>
        public int BufferedBytes => _ring?.Count ?? 0;

        /// <summary>Station metadata, populated after a successful connection.</summary>
        public StationInfo Station => _station;

        /// <summary>
        /// Decoders available to this player, keyed by Content-Type and byte signature.
        /// Contains MP3 by default: register an extra factory to support another codec
        /// (for example Ogg Vorbis) before calling <see cref="Start"/>.
        /// </summary>
        public AudioDecoderRegistry Decoders { get; } = AudioDecoderRegistry.CreateDefault();

        /// <summary>
        /// Raised for each decoded chunk of interleaved 16-bit PCM. The frame's sample
        /// array is reused for zero-GC streaming; copy it during the handler if you need
        /// to retain it past the next frame.
        /// </summary>
        public event Action<PcmFrame> PcmDecoded;

        /// <summary>Raised when the in-stream 'StreamTitle' metadata changes.</summary>
        public event Action<string> StreamTitleChanged;

        /// <summary>Raised on every playback state transition.</summary>
        public event Action<PlaybackState> StateChanged;

        /// <summary>Raised on connection/decoding errors (may fire repeatedly while reconnecting).</summary>
        public event Action<Exception> Error;

        /// <summary>
        /// Raised when the decoder reports container tags (for Ogg Vorbis — the Vorbis comment;
        /// for Icecast Ogg streams it is usually only <c>encoder=</c>). Raised on a background thread.
        /// </summary>
        public event Action<AudioTags> TagsChanged;

        /// <summary>Last tags reported by the decoder, or an empty value when there were none.</summary>
        public AudioTags Tags { get; private set; }

        public void SetUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Station URL is empty.", nameof(url));

            lock (_sync)
            {
                if ((PlaybackState)_state == PlaybackState.Connecting ||
                    (PlaybackState)_state == PlaybackState.Buffering ||
                    (PlaybackState)_state == PlaybackState.Playing)
                    throw new InvalidOperationException("Cannot change the URL while running. Call Stop() first.");
                _url = url.Trim();
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (string.IsNullOrEmpty(_url))
                    throw new InvalidOperationException("Set the station URL first (SetUrl).");

                if ((PlaybackState)_state == PlaybackState.Connecting ||
                    (PlaybackState)_state == PlaybackState.Buffering ||
                    (PlaybackState)_state == PlaybackState.Playing)
                    return;

                _stopRequested = false;
                _readerDone = false;
                _failed = false;
                _lastMeta = null;
                _lastMetaLen = 0;
                _ring = new RingBuffer(RingCapacityBytes);

                _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "InternetRadio.Reader" };
                _decoderThread = new Thread(DecoderLoop)
                {
                    IsBackground = true,
                    Name = "InternetRadio.Decoder",
                    // Keep the decoder ahead of the audio clock so a transient CPU/GC
                    // stall doesn't empty the playback queue.
                    Priority = ThreadPriority.AboveNormal,
                };
                _readerThread.Start();
                _decoderThread.Start();
            }
            SetState(PlaybackState.Connecting);
        }

        public void Stop()
        {
            Thread reader, decoder;
            lock (_sync)
            {
                if ((PlaybackState)_state == PlaybackState.Idle)
                    return;

                _stopRequested = true;
                reader = _readerThread;
                decoder = _decoderThread;
            }

            _ring?.Close();

            if (reader != null && reader != Thread.CurrentThread) reader.Join(3000);
            if (decoder != null && decoder != Thread.CurrentThread) decoder.Join(3000);

            lock (_sync)
            {
                _readerThread = null;
                _decoderThread = null;
                _ring = null;
            }
            SetState(PlaybackState.Idle);
        }

        public void Dispose()
        {
            Stop();
        }

        // ---------------------------------------------------------------------
        // Reader thread: socket -> ring buffer
        // ---------------------------------------------------------------------

        private void ReaderLoop()
        {
            // Allocated once per session and reused across reconnects: the steady-state
            // socket read path must not produce per-chunk garbage.
            byte[] buf = new byte[16 * 1024];
            try
            {
                while (!_stopRequested)
                {
                    StreamClient client = null;
                    try
                    {
                        client = StreamClient.Connect(_url, ConnectTimeoutMs, ReadTimeoutMs, SocketReceiveBufferBytes);
                        _station = client.Station;

                        // Announce the new connection before any of its bytes reach the ring
                        // buffer: the decoder thread uses this to drop the previous codec state.
                        Interlocked.Increment(ref _connectionEpoch);

                        if (State == PlaybackState.Connecting)
                            SetState(PlaybackState.Buffering);

                        while (!_stopRequested)
                        {
                            int n = client.Read(buf, 0, buf.Length);
                            if (n <= 0)
                                throw new EndOfStreamException("The stream ended unexpectedly.");
                            Interlocked.Add(ref _bytesReceived, n);
                            if (!_ring.Write(buf, 0, n))
                                return; // ring closed => stopping
                        }
                        return; // clean stop
                    }
                    catch (Exception ex)
                    {
                        if (_stopRequested)
                            return;
                        RaiseError(ex);
                        if (!AutoReconnect)
                        {
                            _failed = true;
                            return;
                        }
                        Thread.Sleep(ReconnectDelayMs);
                    }
                    finally
                    {
                        client?.Dispose();
                    }
                }
            }
            finally
            {
                _readerDone = true;
                _ring?.Close(); // wake the decoder so it can drain and exit
            }
        }

        // ---------------------------------------------------------------------
        // Decoder thread: ring buffer -> split -> prebuffer -> decode
        // ---------------------------------------------------------------------

        private void DecoderLoop()
        {
            IcySplitter splitter = new IcySplitter();
            IAudioDecoder decoder = null;
            MemoryStream prebuf = new MemoryStream();
            bool prebuffered = false;
            bool initialized = false;
            int epochSeen = 0;
            byte[] sniff = new byte[SniffBytes];
            int sniffCount = 0;
            byte[] chunk = new byte[8192];

            splitter.OnAudio = (data, off, len) =>
            {
                if (decoder == null)
                    return;

                if (!prebuffered)
                {
                    prebuf.Write(data, off, len);
                    if (prebuf.Length >= PrebufferBytes)
                    {
                        prebuffered = true;
                        SetState(PlaybackState.Playing);
                        byte[] buffered = prebuf.ToArray();
                        prebuf.Dispose();
                        prebuf = null;
                        decoder.Feed(buffered, 0, buffered.Length);
                    }
                }
                else
                {
                    decoder.Feed(data, off, len);
                }
            };

            splitter.OnMetadata = (buf, len) =>
            {
                string title = ParseStreamTitle(buf, len);
                if (title != null)
                    StreamTitleChanged?.Invoke(title);
            };

            try
            {
                while (!_stopRequested)
                {
                    int n = _ring.Read(chunk, 0, chunk.Length, 250);
                    if (n <= 0)
                    {
                        if (_readerDone)
                            break;
                        continue;
                    }

                    // A reconnect starts a new bitstream: drop the previous decoder, the
                    // sniffed prefix, the ICY framing and the prebuffer, so the new stream
                    // is not decoded with mid-frame state left over from the old one.
                    int epoch = Volatile.Read(ref _connectionEpoch);
                    if (epoch != epochSeen)
                    {
                        epochSeen = epoch;

                        if (initialized)
                        {
                            DetachTagSource(decoder);
                            decoder.Dispose();
                            decoder = null;
                            initialized = false;
                            SetState(PlaybackState.Buffering);
                        }

                        sniffCount = 0;
                        prebuf?.Dispose();
                        prebuf = new MemoryStream();
                        prebuffered = false;
                    }

                    if (!initialized)
                    {
                        // Detection runs on the leading bytes of the connection. The bytes
                        // consumed here are replayed to the splitter once a decoder is
                        // chosen: they carry the codec headers.
                        StationInfo station = _station;
                        int take = Math.Min(n, sniff.Length - sniffCount);
                        Array.Copy(chunk, 0, sniff, sniffCount, take);
                        sniffCount += take;

                        IAudioDecoderFactory factory = Decoders.Resolve(station?.ContentType, sniff, sniffCount);
                        if (factory == null)
                        {
                            // Keep sniffing until the window is full; only then is the
                            // codec genuinely unrecognized.
                            if (sniffCount >= sniff.Length)
                                throw new NotSupportedException(
                                    "No registered decoder accepts this stream. Registered codecs: " + Decoders.Names +
                                    ". Content-Type: " + (string.IsNullOrEmpty(station?.ContentType) ? "(none)" : station.ContentType) +
                                    ", leading bytes: " + AudioDecoderRegistry.DescribeMagic(sniff, sniffCount) + ".");

                            continue;
                        }

                        decoder = factory.Create();
                        decoder.PcmDecoded += DecoderFrameDecoded;
                        AttachTagSource(decoder);

                        // A declared media type wins, but in-band ICY framing must only be
                        // stripped from codecs that actually use it: for a container format
                        // (Ogg) icy-metaint would cut bytes out of the middle of a page.
                        splitter.Reset(factory.UsesIcyMetadata ? (station?.MetadataInterval ?? 0) : 0);
                        initialized = true;

                        splitter.Feed(sniff, 0, sniffCount);
                        if (take < n)
                            splitter.Feed(chunk, take, n - take);
                        continue;
                    }

                    splitter.Feed(chunk, 0, n);
                }
            }
            catch (Exception ex)
            {
                RaiseError(ex);
                _failed = true;
            }
            finally
            {
                DetachTagSource(decoder);
                decoder?.Dispose();
                SetState(_failed ? PlaybackState.Faulted : PlaybackState.Idle);
            }
        }

        /// <summary>
        /// Decoders that can report container tags (Vorbis comment, ID3, …) are subscribed to
        /// while they are in use; the empty <see cref="StationInfo"/> fields are filled from
        /// what they report, because for a container stream the response headers often carry
        /// less than the container itself.
        /// </summary>
        private void AttachTagSource(IAudioDecoder decoder)
        {
            var source = decoder as IAudioTagSource;
            if (source == null)
                return;

            source.TagsChanged += TagsAvailable;
        }

        private void DetachTagSource(IAudioDecoder decoder)
        {
            var source = decoder as IAudioTagSource;
            if (source == null)
                return;

            source.TagsChanged -= TagsAvailable;
        }

        private void TagsAvailable(AudioTags tags)
        {
            if (tags.IsEmpty)
                return;

            StationInfo station = _station;
            if (station != null)
            {
                if (string.IsNullOrEmpty(station.Name) && !string.IsNullOrEmpty(tags.Title))
                    station.Name = tags.Title;
                if (string.IsNullOrEmpty(station.Genre) && !string.IsNullOrEmpty(tags.Genre))
                    station.Genre = tags.Genre;
            }

            Tags = tags;
            try
            {
                TagsChanged?.Invoke(tags);
            }
            catch (Exception ex)
            {
                RaiseError(ex);
            }
        }

        /// <summary>
        /// Forwards a decoded frame and publishes the stream format the decoder reported.
        /// Container streams (Ogg) carry no <c>icy-sr</c> header, so without this
        /// <see cref="Station"/> would report no sample rate for them.
        /// </summary>
        private void DecoderFrameDecoded(PcmFrame frame)
        {
            StationInfo station = _station;
            if (station != null)
            {
                if (station.SampleRate == 0 && frame.SampleRate > 0)
                    station.SampleRate = frame.SampleRate;
                if (station.Channels == 0 && frame.Channels > 0)
                    station.Channels = frame.Channels;
            }

            try
            {
                PcmDecoded?.Invoke(frame);
            }
            catch (Exception ex)
            {
                RaiseError(ex);
            }
        }

        // ---------------------------------------------------------------------

        private void SetState(PlaybackState state)
        {
            int old = Interlocked.Exchange(ref _state, (int)state);
            if (old != (int)state)
                StateChanged?.Invoke(state);
        }

        private void RaiseError(Exception ex)
        {
            Error?.Invoke(ex);
        }

        private string ParseStreamTitle(byte[] meta, int len)
        {
            if (len <= 0)
                return null;

            // In-band metadata is re-sent every interval. Only decode/parse when the
            // raw block actually changed, so the steady-state path stays allocation-free:
            // a StreamTitle message is rare and irregular by nature.
            if (_lastMeta != null && _lastMetaLen == len && BytesEqual(meta, _lastMeta, len))
                return null;

            var raw = new byte[len];
            Array.Copy(meta, 0, raw, 0, len);
            _lastMeta = raw;
            _lastMetaLen = len;

            return ParseStreamTitle(DecodeMeta(raw, len));
        }

        private static string ParseStreamTitle(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            foreach (string part in raw.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = part.Substring(0, eq).Trim();
                if (!string.Equals(key, "StreamTitle", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = part.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                    value = value.Substring(1, value.Length - 2);
                return value;
            }

            return null;
        }

        private static string DecodeMeta(byte[] buf, int len)
        {
            string s;
            try
            {
                s = Encoding.UTF8.GetString(buf, 0, len);
            }
            catch
            {
                // Latin-1 fallback (Encoding.Latin1 is unavailable on netstandard2.0).
                var chars = new char[len];
                for (int i = 0; i < len; i++)
                    chars[i] = (char)(buf[i] & 0xFF);
                s = new string(chars);
            }

            int nz = s.IndexOf('\0');
            if (nz >= 0)
                s = s.Substring(0, nz);
            return s;
        }

        private static bool BytesEqual(byte[] a, byte[] b, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }
}
