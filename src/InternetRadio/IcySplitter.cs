using System;

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

        /// <summary>Receives a raw in-band metadata block (unparsed bytes).</summary>
        public Action<byte[], int> OnMetadata { get; set; }

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
                        OnMetadata?.Invoke(_metaBuf, _metaLen);
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

    }
}
