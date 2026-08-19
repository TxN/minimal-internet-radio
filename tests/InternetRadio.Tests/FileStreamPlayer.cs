using System;
using System.IO;
using System.Threading;
using InternetRadio;

namespace InternetRadio.Tests
{
    /// <summary>
    /// Streams a local MP3 file through the same audio pipeline used for live radio
    /// (file reader -> ring buffer -> prebuffer -> MP3 decoder), so the buffering and
    /// playback path can be exercised without a network connection or ICY metadata.
    /// PCM is delivered through <see cref="PcmDecoded"/> (typically to WaveOutPlayer).
    /// </summary>
    internal sealed class FileStreamPlayer : IDisposable
    {
        private readonly string _path;
        private readonly int _ringCapacityBytes;
        private readonly int _prebufferBytes;
        private readonly int _readChunkBytes;

        private RingBuffer _ring;
        private Thread _readerThread;
        private Thread _decoderThread;

        private volatile bool _stopRequested;
        private volatile bool _readerDone;
        private volatile bool _done;
        private volatile int _state = (int)PlaybackState.Idle;

        private long _bytesRead;
        private int _frames;

        public event Action<PcmFrame> PcmDecoded;
        public event Action<Exception> Error;
        public event Action<PlaybackState> StateChanged;

        public FileStreamPlayer(string path, int ringCapacityBytes = 1024 * 1024,
            int prebufferBytes = 128 * 1024, int readChunkBytes = 16 * 1024)
        {
            _path = path;
            _ringCapacityBytes = ringCapacityBytes;
            _prebufferBytes = prebufferBytes;
            _readChunkBytes = readChunkBytes;
        }

        public PlaybackState State => (PlaybackState)_state;
        public long BytesRead => Interlocked.Read(ref _bytesRead);
        public int BufferedBytes => _ring?.Count ?? 0;
        public int Frames => Volatile.Read(ref _frames);
        public bool IsDone => _done;

        public void Start()
        {
            if (_readerThread != null)
                return;

            _stopRequested = false;
            _readerDone = false;
            _done = false;
            _ring = new RingBuffer(_ringCapacityBytes);

            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "FileStream.Reader" };
            _decoderThread = new Thread(DecoderLoop)
            {
                IsBackground = true,
                Name = "FileStream.Decoder",
                Priority = ThreadPriority.AboveNormal,
            };

            SetState(PlaybackState.Buffering);
            _readerThread.Start();
            _decoderThread.Start();
        }

        public void Stop()
        {
            _stopRequested = true;
            _ring?.Close();

            if (_readerThread != null && _readerThread != Thread.CurrentThread)
                _readerThread.Join(3000);
            if (_decoderThread != null && _decoderThread != Thread.CurrentThread)
                _decoderThread.Join(3000);

            _readerThread = null;
            _decoderThread = null;
            _ring = null;
            SetState(PlaybackState.Idle);
        }

        public void Dispose() => Stop();

        private void ReaderLoop()
        {
            try
            {
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                           _readChunkBytes, FileOptions.SequentialScan))
                {
                    byte[] buf = new byte[_readChunkBytes];
                    int n;
                    while (!_stopRequested && (n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        Interlocked.Add(ref _bytesRead, n);
                        if (!_ring.Write(buf, 0, n))
                            return; // ring closed => stopping
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_stopRequested)
                    Error?.Invoke(ex);
            }
            finally
            {
                _readerDone = true;
                _ring?.Close(); // EOF/error: wake the decoder so it can drain and exit
            }
        }

        private void DecoderLoop()
        {
            var decoder = new Mp3Decoder();
            var prebuf = new MemoryStream();
            bool prebuffered = false;
            bool failed = false;
            byte[] chunk = new byte[8192];

            decoder.PcmDecoded += frame =>
            {
                Interlocked.Increment(ref _frames);
                try { PcmDecoded?.Invoke(frame); }
                catch (Exception ex) { Error?.Invoke(ex); }
            };

            try
            {
                while (!_stopRequested)
                {
                    int n = _ring.Read(chunk, 0, chunk.Length, 250);
                    if (n <= 0)
                    {
                        if (_readerDone || _stopRequested)
                            break;
                        continue;
                    }

                    if (!prebuffered)
                    {
                        prebuf.Write(chunk, 0, n);
                        if (prebuf.Length >= _prebufferBytes)
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
                        decoder.Feed(chunk, 0, n);
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                Error?.Invoke(ex);
            }
            finally
            {
                decoder.Dispose();
                SetState(failed ? PlaybackState.Faulted : PlaybackState.Idle);
                _done = true;
            }
        }

        private void SetState(PlaybackState state)
        {
            int old = Interlocked.Exchange(ref _state, (int)state);
            if (old != (int)state)
                StateChanged?.Invoke(state);
        }
    }
}
