using System;
using System.Runtime.InteropServices;

namespace InternetRadio.Tests
{
    /// <summary>
    /// Minimal Windows waveform-audio player (winmm.dll) for streaming 16-bit PCM.
    ///
    /// Two design points matter for glitch-free playback:
    ///  1. Completion is event-driven (CALLBACK_EVENT + WaitForSingleObject), not a
    ///     Thread.Sleep(1) poll, so blocks are recycled the moment the driver is done.
    ///  2. PCM is aggregated into ~100 ms blocks before each waveOutWrite. Submitting
    ///     one 26 ms block per MP3 frame makes many drivers skip/splutter at block
    ///     boundaries; a ~100 ms block gives the driver room to chain seamlessly.
    ///
    /// Windows-only.
    /// </summary>
    internal sealed class WaveOutPlayer : IDisposable
    {
        private const uint WhdrDone = 0x00000001;
        private const uint CallbackEvent = 0x00050000;
        private const uint Infinite = 0xFFFFFFFF;
        private const uint TimeMs = 0x0001;
        private const ushort WaveFormatPcm = 1;

        /// <summary>Target duration of one waveOut block (ms).</summary>
        private const int TargetBlockMs = 100;

        /// <summary>Pinned waveOut block buffer size. Holds TargetBlockMs of 48 kHz stereo
        /// plus a whole MP3 frame of slack (5 * 1152 * 2ch * 2B = 23040 &lt; 24576).</summary>
        private const int MaxBufferBytes = 24576;

        public const int BufferCount = 24;   // 24 blocks * ~100 ms = ~2.4 s of headroom

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MmTime
        {
            public uint wType;
            public uint u;
        }

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern uint waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WaveFormatEx pwfx,
            IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern uint waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern uint waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern uint waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern uint waveOutGetPosition(IntPtr hwo, ref MmTime pmmt, uint cbmmt);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr lpSecurityAttributes, bool bManualReset,
            bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly int HeaderSize = Marshal.SizeOf<WaveHdr>();

        private IntPtr _hwo;
        private IntPtr _hEvent;
        private bool _opened;
        private bool _disposed;
        private int _targetBytes;

        private readonly byte[][] _bufs = new byte[BufferCount][];
        private readonly GCHandle[] _bufHandles = new GCHandle[BufferCount];
        private readonly IntPtr[] _hdrPtrs = new IntPtr[BufferCount];
        private readonly bool[] _inFlight = new bool[BufferCount];

        private byte[] _pending;   // staging buffer: aggregates PCM into ~100 ms blocks
        private int _pendingCount;

        private int _totalWrites;
        private int _minInFlight = int.MaxValue;

        /// <summary>Number of waveOut blocks currently queued to the device.</summary>
        public int InFlightCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < BufferCount; i++)
                    if (_inFlight[i]) n++;
                return n;
            }
        }

        /// <summary>
        /// Minimum queue depth observed after the initial fill. A low value (near 0)
        /// means the decoder stalled long enough for the device to drain the queue
        /// (i.e. an audible gap / underrun).
        /// </summary>
        public int MinInFlight => _minInFlight == int.MaxValue ? 0 : _minInFlight;

        /// <summary>
        /// Device play position in milliseconds (waveOutGetPosition, TIME_MS), or -1
        /// if unavailable. Watching how this advances vs wall-clock reveals whether the
        /// device skips ahead (position jumps faster than real time) or stalls.
        /// </summary>
        public long PositionMs
        {
            get
            {
                if (!_opened)
                    return -1;
                var mmt = new MmTime { wType = TimeMs };
                uint r = waveOutGetPosition(_hwo, ref mmt, (uint)Marshal.SizeOf<MmTime>());
                return r == 0 ? mmt.u : -1;
            }
        }

        /// <summary>Opens the default output device for the given PCM format.</summary>
        public void Open(int sampleRate, int channels)
        {
            if (_opened)
                return;

            _targetBytes = Math.Max(1, sampleRate * channels * 2 * TargetBlockMs / 1000);
            if (_targetBytes > MaxBufferBytes)
                _targetBytes = MaxBufferBytes;
            _pending = new byte[MaxBufferBytes];

            var fmt = new WaveFormatEx
            {
                wFormatTag = WaveFormatPcm,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                nBlockAlign = (ushort)(channels * 2),
                wBitsPerSample = 16,
                nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                cbSize = 0,
            };

            _hEvent = CreateEvent(IntPtr.Zero, false, false, null);
            if (_hEvent == IntPtr.Zero)
                throw new InvalidOperationException("CreateEvent failed (Win32 error " + Marshal.GetLastWin32Error() + ").");

            try
            {
                Check(waveOutOpen(out _hwo, 0, ref fmt, _hEvent, IntPtr.Zero, CallbackEvent));
            }
            catch
            {
                CloseHandle(_hEvent);
                _hEvent = IntPtr.Zero;
                throw;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                _bufs[i] = new byte[MaxBufferBytes];
                _bufHandles[i] = GCHandle.Alloc(_bufs[i], GCHandleType.Pinned);
                _hdrPtrs[i] = Marshal.AllocHGlobal(HeaderSize);
            }

            _opened = true;
        }

        /// <summary>Queues interleaved 16-bit PCM; blocks while the device is busy.</summary>
        public void Write(short[] samples, int offset, int count)
        {
            if (count <= 0)
                return;
            if (!_opened)
                throw new InvalidOperationException("WaveOutPlayer is not opened.");

            int byteCount = count * 2;
            if (_pendingCount + byteCount > _pending.Length)
                Flush();

            Buffer.BlockCopy(samples, offset * 2, _pending, _pendingCount, byteCount);
            _pendingCount += byteCount;

            if (_pendingCount >= _targetBytes)
                Flush();
        }

        /// <summary>Submits the aggregated staging buffer as one waveOut block.</summary>
        private void Flush()
        {
            if (_pendingCount == 0)
                return;

            int slot = AcquireSlot();
            Buffer.BlockCopy(_pending, 0, _bufs[slot], 0, _pendingCount);

            var hdr = new WaveHdr
            {
                lpData = _bufHandles[slot].AddrOfPinnedObject(),
                dwBufferLength = (uint)_pendingCount,
                dwBytesRecorded = 0,
                dwFlags = 0,
                dwLoops = 0,
            };
            Marshal.StructureToPtr(hdr, _hdrPtrs[slot], false);

            Check(waveOutPrepareHeader(_hwo, _hdrPtrs[slot], (uint)HeaderSize));
            Check(waveOutWrite(_hwo, _hdrPtrs[slot], (uint)HeaderSize));
            _inFlight[slot] = true;
            _pendingCount = 0;
            _totalWrites++;
        }

        /// <summary>
        /// Waits for a free block; recycles blocks whose WHDR_DONE flag is set.
        /// Blocks on the CALLBACK_EVENT handle instead of busy-polling.
        /// </summary>
        private int AcquireSlot()
        {
            while (true)
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    if (!_inFlight[i])
                    {
                        NoteDepth(i);
                        return i;
                    }

                    WaveHdr hdr = Marshal.PtrToStructure<WaveHdr>(_hdrPtrs[i]);
                    if ((hdr.dwFlags & WhdrDone) != 0)
                    {
                        Check(waveOutUnprepareHeader(_hwo, _hdrPtrs[i], (uint)HeaderSize));
                        _inFlight[i] = false;
                        NoteDepth(i);
                        return i;
                    }
                }

                // All blocks are queued and none finished yet: wait for a completion.
                WaitForSingleObject(_hEvent, Infinite);
            }
        }

        private void NoteDepth(int excluding)
        {
            if (_totalWrites < BufferCount)
                return; // queue still filling for the first time; not a real underrun

            int n = 0;
            for (int i = 0; i < BufferCount; i++)
                if (i != excluding && _inFlight[i])
                    n++;
            if (n < _minInFlight)
                _minInFlight = n;
        }

        private static void Check(uint mmResult)
        {
            if (mmResult != 0)
                throw new InvalidOperationException("winmm error (MMRESULT " + mmResult + ").");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_opened)
            {
                waveOutReset(_hwo);
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_inFlight[i])
                        waveOutUnprepareHeader(_hwo, _hdrPtrs[i], (uint)HeaderSize);
                }
                waveOutClose(_hwo);
            }

            for (int i = 0; i < BufferCount; i++)
            {
                if (_bufHandles[i].IsAllocated)
                    _bufHandles[i].Free();
                if (_hdrPtrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = IntPtr.Zero;
                }
            }

            if (_hEvent != IntPtr.Zero)
            {
                CloseHandle(_hEvent);
                _hEvent = IntPtr.Zero;
            }
        }
    }
}
