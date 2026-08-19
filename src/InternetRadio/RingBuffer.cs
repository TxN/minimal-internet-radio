using System;
using System.Threading;

namespace InternetRadio
{
    /// <summary>
    /// Thread-safe circular byte buffer (FIFO) with blocking writes/reads.
    /// Used to decouple the network reader from the decoder and to smooth jitter.
    /// </summary>
    internal sealed class RingBuffer
    {
        private readonly byte[] _buf;
        private readonly object _sync = new object();
        private int _head;   // next read index
        private int _count;  // available bytes
        private bool _closed;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buf = new byte[capacity];
        }

        public int Capacity => _buf.Length;

        public int Count
        {
            get { lock (_sync) return _count; }
        }

        /// <summary>
        /// Appends bytes, blocking while there is not enough free space.
        /// Returns false if the buffer was closed before all bytes were written.
        /// </summary>
        public bool Write(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            lock (_sync)
            {
                while (count > _buf.Length - _count)
                {
                    if (_closed) return false;
                    Monitor.Wait(_sync);
                }

                int tail = (_head + _count) % _buf.Length;
                int first = Math.Min(count, _buf.Length - tail);
                Array.Copy(data, offset, _buf, tail, first);
                if (count > first)
                    Array.Copy(data, offset + first, _buf, 0, count - first);
                _count += count;
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes, blocking until at least one
        /// byte is available, the buffer is closed, or <paramref name="timeoutMs"/>
        /// elapses. Returns 0 on timeout or when closed with no data.
        /// </summary>
        public int Read(byte[] data, int offset, int count, int timeoutMs)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            lock (_sync)
            {
                while (_count == 0 && !_closed)
                {
                    if (!Monitor.Wait(_sync, timeoutMs))
                        return 0;
                }

                if (_count == 0)
                    return 0;

                int n = Math.Min(count, _count);
                int first = Math.Min(n, _buf.Length - _head);
                Array.Copy(_buf, _head, data, offset, first);
                if (n > first)
                    Array.Copy(_buf, 0, data, offset + first, n - first);
                _head = (_head + n) % _buf.Length;
                _count -= n;
                Monitor.PulseAll(_sync);
                return n;
            }
        }

        /// <summary>Wakes all blocked readers/writers and prevents further writes.</summary>
        public void Close()
        {
            lock (_sync)
            {
                _closed = true;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
