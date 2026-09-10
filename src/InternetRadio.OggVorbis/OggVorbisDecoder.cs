using System;
using InternetRadio.OggVorbis.Vendored;

namespace InternetRadio.OggVorbis
{
    /// <summary>
    /// Ogg Vorbis decoder for the player: adapts the vendored public-domain stb_vorbis port
    /// (see StbVorbis/README.md) to the push-based <see cref="IAudioDecoder"/> contract.
    ///
    /// <see cref="Feed"/> accepts arbitrary byte chunks and consumes whole Ogg packets only;
    /// bytes that do not yet form a complete packet stay in an internal window and are used by
    /// the next call, so the decoded PCM does not depend on how the input is split. This mirrors
    /// the push-api contract of stb_vorbis and the reference harness in tools/ogg_ref.c.
    /// </summary>
    internal sealed unsafe class OggVorbisDecoder : IAudioDecoder, IAudioTagSource
    {
        /// <summary>How many resync attempts a single <see cref="Feed"/> call may make.</summary>
        private const int MaxResyncAttempts = 8;

        private byte[] _buf = new byte[16 * 1024];
        private int _len;
        private int _pos;
        private bool _opened;
        private StbVorbis.stb_vorbis _decoder;
        private int _channels;
        private int _sampleRate;

        // Reused interleaved output buffers, keyed by sample count. A Vorbis stream switches
        // between a long and a short block size (plus a few trimmed frames), so caching by size
        // turns per-frame garbage into a handful of allocations per stream. PcmFrame carries the
        // array itself, so its length must be exactly the frame length.
        private const int FrameCacheSlots = 4;
        private readonly short[][] _frameCache = new short[FrameCacheSlots][];
        private readonly int[] _frameCacheSizes = new int[FrameCacheSlots];
        private int _frameCacheNext;

        public event Action<PcmFrame> PcmDecoded;

        /// <summary>
        /// Raised once per stream with the tags from the Vorbis comment header, right after the
        /// headers were parsed. Icecast Ogg streams usually carry only <c>encoder=</c>, in which
        /// case nothing is reported.
        /// </summary>
        public event Action<AudioTags> TagsChanged;

        public void Feed(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            if (count > 0)
            {
                if (_len + count > _buf.Length)
                    Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));

                // Compact the unconsumed tail so the push window stays contiguous.
                if (_pos > 0)
                {
                    if (_len > _pos)
                        Array.Copy(_buf, _pos, _buf, 0, _len - _pos);
                    _len -= _pos;
                    _pos = 0;
                }

                Array.Copy(data, offset, _buf, _len, count);
                _len += count;
            }

            fixed (byte* buf = _buf)
            {
                Decode(buf);
            }
        }

        /// <summary>Drops all stream state and resynchronises on the next input.</summary>
        public void Reset()
        {
            CloseDecoder();
            _len = 0;
            _pos = 0;
        }

        public void Dispose()
        {
            Reset();
        }

        private void Decode(byte* buf)
        {
            int resyncAttempts = 0;

            // Icecast sources (and one track per file) chain logical streams: a new "OggS" page
            // with the beginning-of-stream flag starts a fresh stream with its own headers and
            // tags. Such a page is never handed to the current decoder — it would just break it —
            // and when the current stream is exhausted the decoder is reopened at that page.
            int nextStream = FindNextStreamStart(buf, _pos + 4, _len);

            while (true)
            {
                if (!_opened)
                {
                    int used = 0;
                    int error = 0;
                    var decoder = StbVorbis.stb_vorbis_open_pushdata(buf + _pos, _len - _pos, &used, &error);

                    if (decoder == null)
                    {
                        if (error == (int)StbVorbis.STBVorbisError.VORBIS_need_more_data)
                            return; // headers are still incomplete

                        // Garbage or a truncated stream: skip to the next stream start.
                        if (++resyncAttempts > MaxResyncAttempts)
                            return;
                        if (!ResyncToNextStream(buf))
                            return;
                        nextStream = FindNextStreamStart(buf, _pos + 4, _len);
                        continue;
                    }

                    _opened = true;
                    _decoder = decoder;
                    _pos += used;

                    var info = StbVorbis.stb_vorbis_get_info(_decoder);
                    _channels = (int)info.channels;
                    _sampleRate = (int)info.sample_rate;

                    ReportTags();
                }

                int limit = nextStream >= 0 ? nextStream : _len;
                int available = limit - _pos;

                if (available <= 0)
                {
                    if (nextStream < 0)
                        return;

                    // The current logical stream is finished: reopen at the next one so its
                    // headers, format and tags are read (that is how per-track radio tags work).
                    CloseDecoder();
                    _pos = nextStream;
                    nextStream = FindNextStreamStart(buf, _pos + 4, _len);
                    continue;
                }

                int channels = 0;
                int samples = 0;
                float*[] output = null;
                int consumed = StbVorbis.stb_vorbis_decode_frame_pushdata(
                    _decoder, buf + _pos, available, &channels, ref output, &samples);

                if (consumed == 0 && samples == 0)
                    return; // need more data

                _pos += consumed;

                if (samples > 0)
                    EmitFrame(output, samples);

                if (_pos >= _len)
                {
                    _len = 0;
                    _pos = 0;
                    nextStream = -1;
                }
            }
        }

        /// <summary>
        /// Moves the window past the current position to the next logical stream start
        /// (<c>OggS</c> with the beginning-of-stream flag), so a corrupt stream always makes
        /// progress instead of growing the window forever. Returns false when the buffer holds
        /// no further stream start, in which case the tail is kept for the next call.
        /// </summary>
        private bool ResyncToNextStream(byte* buf)
        {
            int found = FindNextStreamStart(buf, _pos + 1, _len);
            if (found >= 0)
            {
                _pos = found;
                return true;
            }

            // Keep a short tail in case a capture pattern straddles the chunk boundary.
            _pos = Math.Max(_pos, _len - 6);
            return false;
        }

        /// <summary>
        /// Index of the next Ogg page that begins a logical stream, or -1. A page qualifies when
        /// it carries the beginning-of-stream flag, which is what a decoder needs to (re)start.
        /// </summary>
        private static int FindNextStreamStart(byte* buf, int from, int to)
        {
            for (int i = from; i + 6 <= to; i++)
            {
                if (buf[i] == (byte)'O' && buf[i + 1] == (byte)'g' && buf[i + 2] == (byte)'g' &&
                    buf[i + 3] == (byte)'S' && buf[i + 4] == 0 && (buf[i + 5] & 0x02) != 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Converts the planar float frame into interleaved 16-bit PCM with the project's
        /// convention: truncation toward zero, then the stb clamp. Truncation (not stb's default
        /// rounding path) is what the golden reference in tools/ogg_ref.c produces.
        /// </summary>
        private void EmitFrame(float*[] planar, int samples)
        {
            int channels = _channels;
            int total = samples * channels;
            short[] frame = RentFrame(total);

            int write = 0;
            for (int j = 0; j < samples; j++)
            {
                for (int i = 0; i < channels; i++)
                {
                    int v = (int)(planar[i][j] * 32768f);
                    if ((uint)(v + 32768) > 65535)
                        v = v < 0 ? -32768 : 32767;
                    frame[write++] = (short)v;
                }
            }

            var handler = PcmDecoded;
            if (handler != null)
                handler(new PcmFrame(frame, _sampleRate, channels));
        }

        /// <summary>
        /// Returns a buffer of exactly <paramref name="total"/> samples, reusing one of the
        /// previously used sizes when possible so the steady state allocates nothing.
        /// </summary>
        private short[] RentFrame(int total)
        {
            for (int i = 0; i < _frameCache.Length; i++)
            {
                if (_frameCacheSizes[i] == total)
                    return _frameCache[i];
            }

            var buffer = new short[total];
            int slot = _frameCacheNext;
            _frameCacheNext = (_frameCacheNext + 1) % FrameCacheSlots;
            _frameCache[slot] = buffer;
            _frameCacheSizes[slot] = total;
            return buffer;
        }

        /// <summary>
        /// Publishes the Vorbis comment of the stream that was just opened. Only the fields the
        /// player can use are reported; a comment holding just <c>encoder=…</c> reports nothing.
        /// </summary>
        private void ReportTags()
        {
            var handler = TagsChanged;
            if (handler == null)
                return;

            string[] comments = _decoder.comment_list;
            if (comments == null || comments.Length == 0)
                return;

            string title = null, artist = null, album = null, genre = null;

            for (int i = 0; i < comments.Length; i++)
            {
                string comment = comments[i];
                if (string.IsNullOrEmpty(comment))
                    continue;

                int eq = comment.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = comment.Substring(0, eq).Trim();
                string value = comment.Substring(eq + 1).Trim();
                if (value.Length == 0)
                    continue;

                if (key.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                    title = value;
                else if (key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase))
                    artist = value;
                else if (key.Equals("ALBUM", StringComparison.OrdinalIgnoreCase))
                    album = value;
                else if (key.Equals("GENRE", StringComparison.OrdinalIgnoreCase))
                    genre = value;
            }

            var tags = new AudioTags(title, artist, album, genre);
            if (tags.IsEmpty)
                return;

            handler(tags);
        }

        private void CloseDecoder()
        {
            if (!_opened)
                return;

            StbVorbis.stb_vorbis_close(_decoder);
            _opened = false;
            _decoder = default;
        }
    }
}
