using System;
using System.Text;

namespace InternetRadio
{
    /// <summary>
    /// Splits a raw ICY stream into clean audio bytes and in-band metadata text.
    /// Layout: <c>icy-metaint</c> audio bytes, then one length byte (units of 16),
    /// then <c>length * 16</c> bytes of <c>Key='value';</c> metadata, then audio again.
    /// A zero length byte means no metadata for this interval.
    /// </summary>
    internal sealed class IcySplitter
    {
        private int _metaint;
        private int _bytesToNextMeta;
        private int _metaBytesRemaining;
        private int _metaLen;
        private byte[] _metaBuf;

        /// <summary>Receives clean audio chunks.</summary>
        public Action<byte[], int, int> OnAudio { get; set; }

        /// <summary>Receives the raw metadata block text (unparsed).</summary>
        public Action<string> OnMetadata { get; set; }

        public void Reset(int metaint)
        {
            _metaint = metaint > 0 ? metaint : 0;
            _bytesToNextMeta = _metaint;
            _metaBytesRemaining = 0;
            _metaLen = 0;
        }

        public void Feed(byte[] data, int offset, int count)
        {
            if (count <= 0)
                return;

            if (_metaint <= 0)
            {
                OnAudio?.Invoke(data, offset, count);
                return;
            }

            int pos = offset;
            int end = offset + count;

            while (pos < end)
            {
                if (_metaBytesRemaining > 0)
                {
                    int take = Math.Min(_metaBytesRemaining, end - pos);
                    Array.Copy(data, pos, _metaBuf, _metaLen - _metaBytesRemaining, take);
                    _metaBytesRemaining -= take;
                    pos += take;

                    if (_metaBytesRemaining == 0)
                    {
                        OnMetadata?.Invoke(DecodeMeta(_metaBuf, _metaLen));
                        _bytesToNextMeta = _metaint;
                    }
                }
                else if (_bytesToNextMeta > 0)
                {
                    int take = Math.Min(_bytesToNextMeta, end - pos);
                    OnAudio?.Invoke(data, pos, take);
                    _bytesToNextMeta -= take;
                    pos += take;
                }
                else
                {
                    // One byte metadata length, in units of 16 bytes.
                    int lenByte = data[pos++] & 0xFF;
                    _metaLen = lenByte * 16;
                    if (_metaLen == 0)
                    {
                        _bytesToNextMeta = _metaint;
                    }
                    else
                    {
                        if (_metaBuf == null || _metaBuf.Length < _metaLen)
                            _metaBuf = new byte[_metaLen];
                        _metaBytesRemaining = _metaLen;
                    }
                }
            }
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
    }
}
