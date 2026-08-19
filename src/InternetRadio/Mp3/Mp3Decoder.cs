using System;

namespace InternetRadio
{
    /// <summary>
    /// Compact, dependency-free MPEG Layer III (MP3) decoder.
    /// A faithful scalar C# port of the public-domain minimp3 algorithm (CC0),
    /// restricted to Layer III with 16-bit PCM output. Decodes MPEG-1/2/2.5,
    /// all channel modes (mono / stereo / joint stereo / dual channel), and
    /// resynchronizes on any byte boundary.
    /// </summary>
    internal sealed class Mp3Decoder : IAudioDecoder
    {
        private const int MaxFreeFormatFrameSize = 2304;
        private const int MaxFrameSyncMatches = 10;
        private const int MaxBitreservoirBytes = 511;
        private const int ShortBlockType = 2;
        private const int StopBlockType = 3;
        private const int HdrSize = 4;
        private const int BitsDequantizerOut = -1;
        private const int MaxScf = 255 + BitsDequantizerOut * 4 - 210;   // 41
        private const int MaxScfi = (MaxScf + 3) & ~3;                   // 44

        // Persistent decoder state (mirrors mp3dec_t).
        private readonly float[][] _mdctOverlap = { new float[9 * 32], new float[9 * 32] };
        private readonly float[] _qmfState = new float[15 * 2 * 32];
        private readonly byte[] _reservBuf = new byte[MaxBitreservoirBytes];
        private readonly byte[] _header = new byte[4];
        private int _reserv;
        private int _freeFormatBytes;

        // Per-frame scratch (mirrors mp3dec_scratch_t). Sized with slack so the
        // 4-byte big-endian preload in the Huffman reader never runs off the end.
        private readonly byte[] _maindata = new byte[MaxBitreservoirBytes + MaxFreeFormatFrameSize + 8];
        private readonly float[] _grbuf = new float[2 * 576];
        private readonly float[] _scf = new float[40];
        private readonly float[] _syn = new float[(18 + 15) * (2 * 32)];
        private readonly byte[][] _istPos = { new byte[39], new byte[39] };
        private readonly L3GrInfo[] _grInfo = { new L3GrInfo(), new L3GrInfo(), new L3GrInfo(), new L3GrInfo() };

        // Reusable temporaries.
        private readonly Bs _bsFrame = new Bs();
        private readonly Bs _sBs = new Bs();
        private readonly float[] _co = new float[9];
        private readonly float[] _si = new float[9];
        private readonly float[] _tmp18 = new float[18];
        private readonly float[] _t32 = new float[32];
        private readonly float[] _a4 = new float[4];
        private readonly float[] _b4 = new float[4];
        private readonly short[] _pcm = new short[1152 * 2];
        private readonly FrameInfo _info = new FrameInfo();

        // Reused scalefactor / stereo scratch, so the per-frame decode path allocates nothing.
        private readonly byte[] _scfSize = new byte[4];
        private readonly byte[] _scfInt = new byte[40];
        private readonly int[] _maxBand = new int[3];

        // Reused PCM output buffer. It is reallocated only when the frame size changes
        // (mono -> stereo or sample layout change), which is at most once per stream.
        private short[] _frame;
        private int _frameLen;

        // Compressed input buffer.
        private byte[] _inBuf = new byte[16 * 1024];
        private int _inCount;

        public event Action<PcmFrame> PcmDecoded;

        public void Feed(byte[] data, int offset, int count)
        {
            if (count <= 0)
                return;

            if (_inCount + count > _inBuf.Length)
            {
                int newSize = Math.Max(_inBuf.Length * 2, _inCount + count);
                Array.Resize(ref _inBuf, newSize);
            }
            Array.Copy(data, offset, _inBuf, _inCount, count);
            _inCount += count;
            DecodeAvailable();
        }

        public void Reset()
        {
            _inCount = 0;
            ClearDecoderState();
        }

        public void Dispose()
        {
        }

        // ---------------------------------------------------------------------
        // Streaming wrapper
        // ---------------------------------------------------------------------

        private void DecodeAvailable()
        {
            while (_inCount > HdrSize)
            {
                int samples = DecodeFrame(_inBuf, _inCount, _pcm, _info);
                int used = _info.FrameBytes;

                if (samples > 0)
                {
                    int total = samples * _info.Channels;
                    if (_frame == null || _frameLen != total)
                    {
                        _frame = new short[total];
                        _frameLen = total;
                    }
                    Array.Copy(_pcm, _frame, total);
                    PcmDecoded?.Invoke(new PcmFrame(_frame, _info.Hz, _info.Channels));
                }

                int advance;
                if (samples > 0)
                {
                    // A frame was decoded: consume exactly its bytes. This branch is
                    // essential even when used == _inCount (the frame filled the whole
                    // buffer); otherwise a spurious 4-byte tail is kept and the stream
                    // gets misaligned.
                    advance = used;
                }
                else if (used >= _inCount)
                {
                    // No sync found: keep a short tail that may hold a partial header.
                    advance = _inCount - Math.Min(HdrSize, _inCount);
                }
                else
                {
                    // Skipped garbage (or a bad frame) up to the next sync.
                    advance = used;
                }

                if (advance <= 0)
                    break;

                Array.Copy(_inBuf, advance, _inBuf, 0, _inCount - advance);
                _inCount -= advance;
            }
        }

        private void ClearDecoderState()
        {
            Array.Clear(_mdctOverlap[0], 0, _mdctOverlap[0].Length);
            Array.Clear(_mdctOverlap[1], 0, _mdctOverlap[1].Length);
            Array.Clear(_qmfState, 0, _qmfState.Length);
            Array.Clear(_reservBuf, 0, _reservBuf.Length);
            Array.Clear(_header, 0, _header.Length);
            _reserv = 0;
            _freeFormatBytes = 0;
        }

        // ---------------------------------------------------------------------
        // Bit reader
        // ---------------------------------------------------------------------

        private static uint GetBits(Bs bs, int n)
        {
            uint next, cache = 0;
            int s = bs.Pos & 7;
            int shl = n + s;
            int p = bs.Pos >> 3;
            bs.Pos += n;
            if (bs.Pos > bs.Limit)
                return 0;
            next = (uint)(bs.Buf[p++] & (255 >> s));
            while ((shl -= 8) > 0)
            {
                cache |= next << shl;
                next = bs.Buf[p++];
            }
            return cache | (next >> -shl);
        }

        private static void BsInit(Bs bs, byte[] buf, int bitPos, int bitLimit)
        {
            bs.Buf = buf;
            bs.Pos = bitPos;
            bs.Limit = bitLimit;
        }

        // ---------------------------------------------------------------------
        // Header helpers (all offset-based into a byte buffer)
        // ---------------------------------------------------------------------

        private static bool HdrIsMono(byte[] h, int o) => (h[o + 3] & 0xC0) == 0xC0;
        private static bool HdrIsMsStereo(byte[] h, int o) => (h[o + 3] & 0xE0) == 0x60;
        private static bool HdrIsFreeFormat(byte[] h, int o) => (h[o + 2] & 0xF0) == 0;
        private static bool HdrIsCrc(byte[] h, int o) => (h[o + 1] & 1) == 0;
        private static bool HdrTestPadding(byte[] h, int o) => (h[o + 2] & 0x2) != 0;
        private static bool HdrTestMpeg1(byte[] h, int o) => (h[o + 1] & 0x8) != 0;
        private static bool HdrTestNotMpeg25(byte[] h, int o) => (h[o + 1] & 0x10) != 0;
        private static bool HdrTestIStereo(byte[] h, int o) => (h[o + 3] & 0x10) != 0;
        private static bool HdrTestMsStereo(byte[] h, int o) => (h[o + 3] & 0x20) != 0;
        private static bool HdrIsLayer1(byte[] h, int o) => (h[o + 1] & 6) == 6;
        private static bool HdrIsFrame576(byte[] h, int o) => (h[o + 1] & 14) == 2;

        private static int HdrGetLayer(byte[] h, int o) => (h[o + 1] >> 1) & 3;
        private static int HdrGetBitrate(byte[] h, int o) => h[o + 2] >> 4;
        private static int HdrGetSampleRate(byte[] h, int o) => (h[o + 2] >> 2) & 3;
        private static int HdrGetMySampleRate(byte[] h, int o) =>
            HdrGetSampleRate(h, o) + (((h[o + 1] >> 3) & 1) + ((h[o + 1] >> 4) & 1)) * 3;

        private static bool HdrValid(byte[] h, int o) =>
            h[o] == 0xFF &&
            ((h[o + 1] & 0xF0) == 0xF0 || (h[o + 1] & 0xFE) == 0xE2) &&
            HdrGetLayer(h, o) != 0 &&
            HdrGetBitrate(h, o) != 15 &&
            HdrGetSampleRate(h, o) != 3;

        private static bool HdrCompare(byte[] h1, int o1, byte[] h2, int o2) =>
            HdrValid(h2, o2) &&
            ((h1[o1 + 1] ^ h2[o2 + 1]) & 0xFE) == 0 &&
            ((h1[o1 + 2] ^ h2[o2 + 2]) & 0x0C) == 0 &&
            !(HdrIsFreeFormat(h1, o1) ^ HdrIsFreeFormat(h2, o2));

        private static int HdrBitrateKbps(byte[] h, int o)
        {
            int mpeg1 = HdrTestMpeg1(h, o) ? 1 : 0;
            int layer = HdrGetLayer(h, o) - 1;
            return 2 * Mp3Tables.Halfrate[(mpeg1 * 3 + layer) * 15 + HdrGetBitrate(h, o)];
        }

        private static int HdrSampleRateHz(byte[] h, int o)
        {
            int v = HdrGetSampleRate(h, o) switch
            {
                0 => 44100,
                1 => 48000,
                _ => 32000,
            };
            if (!HdrTestMpeg1(h, o)) v >>= 1;
            if (!HdrTestNotMpeg25(h, o)) v >>= 1;
            return v;
        }

        private static int HdrFrameSamples(byte[] h, int o) =>
            HdrIsLayer1(h, o) ? 384 : (HdrIsFrame576(h, o) ? 576 : 1152);

        private static int HdrFrameBytes(byte[] h, int o, int freeFormatSize)
        {
            int fb = HdrFrameSamples(h, o) * HdrBitrateKbps(h, o) * 125 / HdrSampleRateHz(h, o);
            if (HdrIsLayer1(h, o))
                fb &= ~3;
            return fb != 0 ? fb : freeFormatSize;
        }

        private static int HdrPadding(byte[] h, int o) =>
            HdrTestPadding(h, o) ? (HdrIsLayer1(h, o) ? 4 : 1) : 0;

        // ---------------------------------------------------------------------
        // Side information and scalefactors
        // ---------------------------------------------------------------------

        private static int L3ReadSideInfo(Bs bs, L3GrInfo[] gr, byte[] hdr, int hdrOff)
        {
            int tables, scfsi = 0;
            int mainDataBegin, part23Sum = 0;
            int srIdx = HdrGetMySampleRate(hdr, hdrOff);
            srIdx -= srIdx != 0 ? 1 : 0;
            int grCount = HdrIsMono(hdr, hdrOff) ? 1 : 2;
            int gi = 0;

            if (HdrTestMpeg1(hdr, hdrOff))
            {
                grCount *= 2;
                mainDataBegin = (int)GetBits(bs, 9);
                scfsi = (int)GetBits(bs, 7 + grCount);
            }
            else
            {
                mainDataBegin = (int)(GetBits(bs, 8 + grCount) >> grCount);
            }

            do
            {
                if (HdrIsMono(hdr, hdrOff))
                    scfsi <<= 4;

                L3GrInfo g = gr[gi];
                g.Part23Length = (ushort)GetBits(bs, 12);
                part23Sum += g.Part23Length;
                g.BigValues = (ushort)GetBits(bs, 9);
                if (g.BigValues > 288)
                    return -1;
                g.GlobalGain = (byte)GetBits(bs, 8);
                g.ScalefacCompress = (ushort)GetBits(bs, HdrTestMpeg1(hdr, hdrOff) ? 4 : 9);
                g.SfbTab = Mp3Tables.ScfLong;
                g.SfbOffset = srIdx * 23;
                g.NLongSfb = 22;
                g.NShortSfb = 0;

                if (GetBits(bs, 1) != 0)
                {
                    g.BlockType = (byte)GetBits(bs, 2);
                    if (g.BlockType == 0)
                        return -1;
                    g.MixedBlockFlag = (byte)GetBits(bs, 1);
                    g.RegionCount[0] = 7;
                    g.RegionCount[1] = 255;
                    if (g.BlockType == ShortBlockType)
                    {
                        scfsi &= 0x0F0F;
                        if (g.MixedBlockFlag == 0)
                        {
                            g.RegionCount[0] = 8;
                            g.SfbTab = Mp3Tables.ScfShort;
                            g.SfbOffset = srIdx * 40;
                            g.NLongSfb = 0;
                            g.NShortSfb = 39;
                        }
                        else
                        {
                            g.SfbTab = Mp3Tables.ScfMixed;
                            g.SfbOffset = srIdx * 40;
                            g.NLongSfb = (byte)(HdrTestMpeg1(hdr, hdrOff) ? 8 : 6);
                            g.NShortSfb = 30;
                        }
                    }
                    tables = (int)GetBits(bs, 10);
                    tables <<= 5;
                    g.SubblockGain[0] = (byte)GetBits(bs, 3);
                    g.SubblockGain[1] = (byte)GetBits(bs, 3);
                    g.SubblockGain[2] = (byte)GetBits(bs, 3);
                }
                else
                {
                    g.BlockType = 0;
                    g.MixedBlockFlag = 0;
                    tables = (int)GetBits(bs, 15);
                    g.RegionCount[0] = (byte)GetBits(bs, 4);
                    g.RegionCount[1] = (byte)GetBits(bs, 3);
                    g.RegionCount[2] = 255;
                }

                g.TableSelect[0] = (byte)(tables >> 10);
                g.TableSelect[1] = (byte)((tables >> 5) & 31);
                g.TableSelect[2] = (byte)(tables & 31);
                g.Preflag = (byte)(HdrTestMpeg1(hdr, hdrOff) ? GetBits(bs, 1) : (g.ScalefacCompress >= 500 ? 1u : 0u));
                g.ScalefacScale = (byte)GetBits(bs, 1);
                g.Count1Table = (byte)GetBits(bs, 1);
                g.Scfsi = (byte)((scfsi >> 12) & 15);
                scfsi <<= 4;
                gi++;
            } while (--grCount != 0);

            if (part23Sum + bs.Pos > bs.Limit + mainDataBegin * 8)
                return -1;

            return mainDataBegin;
        }

        private static float L3LdexpQ2(float y, int expQ2)
        {
            int e;
            do
            {
                e = Math.Min(30 * 4, expQ2);
                y *= Mp3Tables.ExpFrac[e & 3] * (1 << 30 >> (e >> 2));
            } while ((expQ2 -= e) > 0);
            return y;
        }

        private static void L3ReadScalefactors(byte[] scf, byte[] istPos, byte[] scfSize,
            byte[] scfCountTable, int scfCountOffset, Bs bitbuf, int scfsi)
        {
            int i, k;
            int scfPos = 0, istPosPos = 0;
            for (i = 0; i < 4 && scfCountTable[scfCountOffset + i] != 0; i++, scfsi *= 2)
            {
                int cnt = scfCountTable[scfCountOffset + i];
                if ((scfsi & 8) != 0)
                {
                    Array.Copy(istPos, istPosPos, scf, scfPos, cnt);
                }
                else
                {
                    int bits = scfSize[i];
                    if (bits == 0)
                    {
                        for (k = 0; k < cnt; k++)
                        {
                            scf[scfPos + k] = 0;
                            istPos[istPosPos + k] = 0;
                        }
                    }
                    else
                    {
                        int maxScf = scfsi < 0 ? (1 << bits) - 1 : -1;
                        for (k = 0; k < cnt; k++)
                        {
                            int s = (int)GetBits(bitbuf, bits);
                            istPos[istPosPos + k] = (byte)(s == maxScf ? 255 : s);
                            scf[scfPos + k] = (byte)s;
                        }
                    }
                }
                istPosPos += cnt;
                scfPos += cnt;
            }
            scf[scfPos] = scf[scfPos + 1] = scf[scfPos + 2] = 0;
        }

        private void L3DecodeScalefactors(byte[] hdr, int hdrOff, byte[] istPos,
            Bs bs, L3GrInfo gr, float[] scf, int ch)
        {
            byte[] scfSize = _scfSize;
            byte[] iscf = _scfInt;
            int scfShift = gr.ScalefacScale + 1;
            int scfsi = gr.Scfsi;

            int rowIdx = (gr.NShortSfb != 0 ? 1 : 0) + (gr.NLongSfb == 0 ? 1 : 0);
            int scfPartitionOffset = rowIdx * 28;
            int k = 0;

            if (HdrTestMpeg1(hdr, hdrOff))
            {
                int part = Mp3Tables.ScfcDecode[gr.ScalefacCompress];
                scfSize[1] = scfSize[0] = (byte)(part >> 2);
                scfSize[3] = scfSize[2] = (byte)(part & 3);
            }
            else
            {
                int sfc, ModProd, ist = HdrTestIStereo(hdr, hdrOff) && ch != 0 ? 1 : 0;
                sfc = gr.ScalefacCompress >> ist;
                for (k = ist * 3 * 4; sfc >= 0; sfc -= ModProd, k += 4)
                {
                    ModProd = 1;
                    for (int i = 3; i >= 0; i--)
                    {
                        scfSize[i] = (byte)(sfc / ModProd % Mp3Tables.Mod[k + i]);
                        ModProd *= Mp3Tables.Mod[k + i];
                    }
                }
                scfPartitionOffset += k;
                scfsi = -16;
            }

            L3ReadScalefactors(iscf, istPos, scfSize, Mp3Tables.ScfPartitions, scfPartitionOffset, bs, scfsi);

            if (gr.NShortSfb != 0)
            {
                int sh = 3 - scfShift;
                for (int i = 0; i < gr.NShortSfb; i += 3)
                {
                    iscf[gr.NLongSfb + i + 0] += (byte)(gr.SubblockGain[0] << sh);
                    iscf[gr.NLongSfb + i + 1] += (byte)(gr.SubblockGain[1] << sh);
                    iscf[gr.NLongSfb + i + 2] += (byte)(gr.SubblockGain[2] << sh);
                }
            }
            else if (gr.Preflag != 0)
            {
                for (int i = 0; i < 10; i++)
                    iscf[11 + i] += Mp3Tables.Preamp[i];
            }

            int gainExp = gr.GlobalGain + BitsDequantizerOut * 4 - 210 - (HdrIsMsStereo(hdr, hdrOff) ? 2 : 0);
            float gain = L3LdexpQ2(1 << (MaxScfi / 4), MaxScfi - gainExp);
            for (int i = 0; i < gr.NLongSfb + gr.NShortSfb; i++)
                scf[i] = L3LdexpQ2(gain, iscf[i] << scfShift);
        }

        // ---------------------------------------------------------------------
        // Huffman decoding
        // ---------------------------------------------------------------------

        private static int Peek(uint cache, int n) => (int)(cache >> (32 - n));

        private static void Flush(ref uint cache, ref int sh, int n)
        {
            cache <<= n;
            sh += n;
        }

        private static void CheckBits(byte[] buf, ref int next, ref uint cache, ref int sh)
        {
            while (sh >= 0)
            {
                cache |= (uint)buf[next++] << sh;
                sh -= 8;
            }
        }

        private static int BsPos(int next, int sh) => next * 8 - 24 + sh;

        private static float L3Pow43(int x)
        {
            float frac;
            int sign, mult = 256;
            if (x < 129)
                return Mp3Tables.Pow43[16 + x];
            if (x < 1024)
            {
                mult = 16;
                x <<= 3;
            }
            sign = 2 * x & 64;
            frac = (float)((x & 63) - sign) / ((x & ~63) + sign);
            return Mp3Tables.Pow43[16 + ((x + sign) >> 6)] * (1f + frac * ((4f / 3f) + frac * (2f / 9f))) * mult;
        }

        private static void L3Huffman(float[] dst, int dstBase, Bs bs, L3GrInfo gr,
            float[] scf, int layer3grLimit)
        {
            float one = 0f;
            int ireg = 0;
            int bigValCnt = gr.BigValues;
            int sfbIdx = gr.SfbOffset;
            int scfIdx = 0;
            int np, pairsToDecode;
            int bsPos = bs.Pos;
            int bsNext = bsPos >> 3;
            uint bsCache = (uint)(((bs.Buf[bsNext] * 256u + bs.Buf[bsNext + 1]) * 256u + bs.Buf[bsNext + 2]) * 256u + bs.Buf[bsNext + 3]) << (bsPos & 7);
            int bsSh = (bsPos & 7) - 8;
            bsNext += 4;

            while (bigValCnt > 0)
            {
                int tabNum = gr.TableSelect[ireg];
                int sfbCnt = gr.RegionCount[ireg++];
                int codebook = Mp3Tables.TabIndex[tabNum];
                int linbits = Mp3Tables.Linbits[tabNum];

                if (linbits != 0)
                {
                    do
                    {
                        np = gr.SfbTab[sfbIdx++] / 2;
                        pairsToDecode = Math.Min(bigValCnt, np);
                        one = scf[scfIdx++];
                        do
                        {
                            int j, w = 5;
                            int leaf = Mp3Tables.Tabs[codebook + Peek(bsCache, w)];
                            while (leaf < 0)
                            {
                                Flush(ref bsCache, ref bsSh, w);
                                w = leaf & 7;
                                leaf = Mp3Tables.Tabs[codebook + Peek(bsCache, w) - (leaf >> 3)];
                            }
                            Flush(ref bsCache, ref bsSh, leaf >> 8);

                            for (j = 0; j < 2; j++, dstBase++, leaf >>= 4)
                            {
                                int lsb = leaf & 0x0F;
                                if (lsb == 15)
                                {
                                    lsb += Peek(bsCache, linbits);
                                    Flush(ref bsCache, ref bsSh, linbits);
                                    CheckBits(bs.Buf, ref bsNext, ref bsCache, ref bsSh);
                                    dst[dstBase] = one * L3Pow43(lsb) * ((int)bsCache < 0 ? -1f : 1f);
                                }
                                else
                                {
                                    dst[dstBase] = Mp3Tables.Pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
                                }
                                Flush(ref bsCache, ref bsSh, lsb != 0 ? 1 : 0);
                            }
                            CheckBits(bs.Buf, ref bsNext, ref bsCache, ref bsSh);
                        } while (--pairsToDecode != 0);
                    } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
                }
                else
                {
                    do
                    {
                        np = gr.SfbTab[sfbIdx++] / 2;
                        pairsToDecode = Math.Min(bigValCnt, np);
                        one = scf[scfIdx++];
                        do
                        {
                            int j, w = 5;
                            int leaf = Mp3Tables.Tabs[codebook + Peek(bsCache, w)];
                            while (leaf < 0)
                            {
                                Flush(ref bsCache, ref bsSh, w);
                                w = leaf & 7;
                                leaf = Mp3Tables.Tabs[codebook + Peek(bsCache, w) - (leaf >> 3)];
                            }
                            Flush(ref bsCache, ref bsSh, leaf >> 8);

                            for (j = 0; j < 2; j++, dstBase++, leaf >>= 4)
                            {
                                int lsb = leaf & 0x0F;
                                dst[dstBase] = Mp3Tables.Pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
                                Flush(ref bsCache, ref bsSh, lsb != 0 ? 1 : 0);
                            }
                            CheckBits(bs.Buf, ref bsNext, ref bsCache, ref bsSh);
                        } while (--pairsToDecode != 0);
                    } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
                }
            }

            int npCount = 1 - bigValCnt;
            while (true)
            {
                byte[] codebookCount1 = gr.Count1Table != 0 ? Mp3Tables.Tab33 : Mp3Tables.Tab32;
                int leaf = codebookCount1[Peek(bsCache, 4)];
                if ((leaf & 8) == 0)
                {
                    leaf = codebookCount1[(leaf >> 3) + (int)(bsCache << 4 >> (32 - (leaf & 3)))];
                }
                Flush(ref bsCache, ref bsSh, leaf & 7);
                if (BsPos(bsNext, bsSh) > layer3grLimit)
                    break;

                if (--npCount == 0)
                {
                    npCount = gr.SfbTab[sfbIdx++] / 2;
                    if (npCount == 0) break;
                    one = scf[scfIdx++];
                }
                if ((leaf & 128) != 0) { dst[dstBase] = (int)bsCache < 0 ? -one : one; Flush(ref bsCache, ref bsSh, 1); }
                if ((leaf & 64) != 0) { dst[dstBase + 1] = (int)bsCache < 0 ? -one : one; Flush(ref bsCache, ref bsSh, 1); }
                if (--npCount == 0)
                {
                    npCount = gr.SfbTab[sfbIdx++] / 2;
                    if (npCount == 0) break;
                    one = scf[scfIdx++];
                }
                if ((leaf & 32) != 0) { dst[dstBase + 2] = (int)bsCache < 0 ? -one : one; Flush(ref bsCache, ref bsSh, 1); }
                if ((leaf & 16) != 0) { dst[dstBase + 3] = (int)bsCache < 0 ? -one : one; Flush(ref bsCache, ref bsSh, 1); }
                CheckBits(bs.Buf, ref bsNext, ref bsCache, ref bsSh);
                dstBase += 4;
            }

            bs.Pos = layer3grLimit;
        }

        // ---------------------------------------------------------------------
        // Stereo processing
        // ---------------------------------------------------------------------

        private static void L3MidSideStereo(float[] grbuf, int leftBase, int n)
        {
            int right = leftBase + 576;
            for (int i = 0; i < n; i++)
            {
                float a = grbuf[leftBase + i];
                float b = grbuf[right + i];
                grbuf[leftBase + i] = a + b;
                grbuf[right + i] = a - b;
            }
        }

        private static void L3IntensityStereoBand(float[] grbuf, int leftBase, int n, float kl, float kr)
        {
            for (int i = 0; i < n; i++)
            {
                grbuf[leftBase + i + 576] = grbuf[leftBase + i] * kr;
                grbuf[leftBase + i] = grbuf[leftBase + i] * kl;
            }
        }

        private static void L3StereoTopBand(float[] grbuf, int rightBase, byte[] sfbTab,
            int sfbBase, int nbands, int[] maxBand)
        {
            maxBand[0] = maxBand[1] = maxBand[2] = -1;
            int b = rightBase;
            for (int i = 0; i < nbands; i++)
            {
                int w = sfbTab[sfbBase + i];
                for (int k = 0; k < w; k += 2)
                {
                    if (grbuf[b + k] != 0 || grbuf[b + k + 1] != 0)
                    {
                        maxBand[i % 3] = i;
                        break;
                    }
                }
                b += w;
            }
        }

        private static void L3StereoProcess(float[] grbuf, byte[] istPos, byte[] sfbTab,
            int sfbBase, byte[] hdr, int hdrOff, int[] maxBand, int mpeg2Sh)
        {
            int maxPos = HdrTestMpeg1(hdr, hdrOff) ? 7 : 64;
            int leftBase = 0;
            for (int i = 0; sfbTab[sfbBase + i] != 0; i++)
            {
                int ipos = istPos[i];
                int band = sfbTab[sfbBase + i];
                if (i > maxBand[i % 3] && ipos < maxPos)
                {
                    float kl, kr;
                    float s = HdrTestMsStereo(hdr, hdrOff) ? 1.41421356f : 1f;
                    if (HdrTestMpeg1(hdr, hdrOff))
                    {
                        kl = Mp3Tables.Pan[2 * ipos];
                        kr = Mp3Tables.Pan[2 * ipos + 1];
                    }
                    else
                    {
                        kl = 1f;
                        kr = L3LdexpQ2(1f, ((ipos + 1) >> 1) << mpeg2Sh);
                        if ((ipos & 1) != 0)
                        {
                            kl = kr;
                            kr = 1f;
                        }
                    }
                    L3IntensityStereoBand(grbuf, leftBase, band, kl * s, kr * s);
                }
                else if (HdrTestMsStereo(hdr, hdrOff))
                {
                    L3MidSideStereo(grbuf, leftBase, band);
                }
                leftBase += band;
            }
        }

        private void L3IntensityStereo(float[] grbuf, byte[] istPos, L3GrInfo[] grInfo,
            int grBase, byte[] hdr, int hdrOff)
        {
            L3GrInfo gr = grInfo[grBase];
            int[] maxBand = _maxBand;
            int nSfb = gr.NLongSfb + gr.NShortSfb;
            int maxBlocks = gr.NShortSfb != 0 ? 3 : 1;

            L3StereoTopBand(grbuf, 576, gr.SfbTab, gr.SfbOffset, nSfb, maxBand);
            if (gr.NLongSfb != 0)
            {
                int m = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
                maxBand[0] = maxBand[1] = maxBand[2] = m;
            }
            for (int i = 0; i < maxBlocks; i++)
            {
                int defaultPos = HdrTestMpeg1(hdr, hdrOff) ? 3 : 0;
                int itop = nSfb - maxBlocks + i;
                int prev = itop - maxBlocks;
                istPos[itop] = (byte)(maxBand[i] >= prev ? defaultPos : istPos[prev]);
            }
            int mpeg2Sh = grInfo[grBase + 1].ScalefacCompress & 1;
            L3StereoProcess(grbuf, istPos, gr.SfbTab, gr.SfbOffset, hdr, hdrOff, maxBand, mpeg2Sh);
        }

        // ---------------------------------------------------------------------
        // Reorder, antialias, IMDCT
        // ---------------------------------------------------------------------

        private static void L3Reorder(float[] grbuf, int grbufBase, float[] scratch,
            byte[] sfbTab, int sfbBase)
        {
            int src = grbufBase;
            int dst = 0;
            int sfb = sfbBase;
            int len;
            while ((len = sfbTab[sfb]) != 0)
            {
                sfb += 3;
                for (int i = 0; i < len; i++, src++)
                {
                    scratch[dst++] = grbuf[src];
                    scratch[dst++] = grbuf[src + len];
                    scratch[dst++] = grbuf[src + 2 * len];
                }
                src += 2 * len;
            }
            Array.Copy(scratch, 0, grbuf, grbufBase, dst);
        }

        private static void L3Antialias(float[] grbuf, int grbufBase, int nbands)
        {
            for (; nbands > 0; nbands--, grbufBase += 18)
            {
                for (int i = 0; i < 8; i++)
                {
                    float u = grbuf[grbufBase + 18 + i];
                    float d = grbuf[grbufBase + 17 - i];
                    grbuf[grbufBase + 18 + i] = u * Mp3Tables.Aa[i] - d * Mp3Tables.Aa[8 + i];
                    grbuf[grbufBase + 17 - i] = u * Mp3Tables.Aa[8 + i] + d * Mp3Tables.Aa[i];
                }
            }
        }

        private static void L3Dct39(float[] y)
        {
            float s0 = y[0], s2 = y[2], s4 = y[4], s6 = y[6], s8 = y[8];
            float t0 = s0 + s6 * 0.5f;
            s0 -= s6;
            float t4 = (s4 + s2) * 0.93969262f;
            float t2 = (s8 + s2) * 0.76604444f;
            s6 = (s4 - s8) * 0.17364818f;
            s4 += s8 - s2;

            s2 = s0 - s4 * 0.5f;
            y[4] = s4 + s0;
            s8 = t0 - t2 + s6;
            s0 = t0 - t4 + t2;
            s4 = t0 + t4 - s6;

            float s1 = y[1], s3 = y[3], s5 = y[5], s7 = y[7];
            s3 *= 0.86602540f;
            t0 = (s5 + s1) * 0.98480775f;
            t4 = (s5 - s7) * 0.34202014f;
            t2 = (s1 + s7) * 0.64278761f;
            s1 = (s1 - s5 - s7) * 0.86602540f;

            s5 = t0 - s3 - t2;
            s7 = t4 - s3 - t0;
            s3 = t4 + s3 - t2;

            y[0] = s4 - s7;
            y[1] = s2 + s1;
            y[2] = s0 - s3;
            y[3] = s8 + s5;
            y[5] = s8 - s5;
            y[6] = s0 + s3;
            y[7] = s2 - s1;
            y[8] = s4 + s7;
        }

        private static void L3Imdct36(float[] grbuf, int grbufBase, float[] overlap,
            int overlapBase, int windowBase, int nbands, float[] co, float[] si)
        {
            for (int j = 0; j < nbands; j++, grbufBase += 18, overlapBase += 9)
            {
                co[0] = -grbuf[grbufBase];
                si[0] = grbuf[grbufBase + 17];
                for (int i = 0; i < 4; i++)
                {
                    si[8 - 2 * i] = grbuf[grbufBase + 4 * i + 1] - grbuf[grbufBase + 4 * i + 2];
                    co[1 + 2 * i] = grbuf[grbufBase + 4 * i + 1] + grbuf[grbufBase + 4 * i + 2];
                    si[7 - 2 * i] = grbuf[grbufBase + 4 * i + 4] - grbuf[grbufBase + 4 * i + 3];
                    co[2 + 2 * i] = -(grbuf[grbufBase + 4 * i + 3] + grbuf[grbufBase + 4 * i + 4]);
                }
                L3Dct39(co);
                L3Dct39(si);
                si[1] = -si[1];
                si[3] = -si[3];
                si[5] = -si[5];
                si[7] = -si[7];

                for (int i = 0; i < 9; i++)
                {
                    float ovl = overlap[overlapBase + i];
                    float sum = co[i] * Mp3Tables.Twid9[9 + i] + si[i] * Mp3Tables.Twid9[i];
                    overlap[overlapBase + i] = co[i] * Mp3Tables.Twid9[i] - si[i] * Mp3Tables.Twid9[9 + i];
                    grbuf[grbufBase + i] = ovl * Mp3Tables.MdctWindow[windowBase + i] - sum * Mp3Tables.MdctWindow[windowBase + 9 + i];
                    grbuf[grbufBase + 17 - i] = ovl * Mp3Tables.MdctWindow[windowBase + 9 + i] + sum * Mp3Tables.MdctWindow[windowBase + i];
                }
            }
        }

        private static void L3Idct3(float x0, float x1, float x2, float[] dst, int dstBase)
        {
            float m1 = x1 * 0.86602540f;
            float a1 = x0 - x2 * 0.5f;
            dst[dstBase + 1] = x0 + x2;
            dst[dstBase] = a1 + m1;
            dst[dstBase + 2] = a1 - m1;
        }

        private static void L3Imdct12(float[] x, int xBase, float[] dst, int dstBase,
            float[] overlap, int overlapBase, float[] co, float[] si)
        {
            L3Idct3(-x[xBase], x[xBase + 6] + x[xBase + 3], x[xBase + 12] + x[xBase + 9], co, 0);
            L3Idct3(x[xBase + 15], x[xBase + 12] - x[xBase + 9], x[xBase + 6] - x[xBase + 3], si, 0);
            si[1] = -si[1];
            for (int i = 0; i < 3; i++)
            {
                float ovl = overlap[overlapBase + i];
                float sum = co[i] * Mp3Tables.Twid3[3 + i] + si[i] * Mp3Tables.Twid3[i];
                overlap[overlapBase + i] = co[i] * Mp3Tables.Twid3[i] - si[i] * Mp3Tables.Twid3[3 + i];
                dst[dstBase + i] = ovl * Mp3Tables.Twid3[2 - i] - sum * Mp3Tables.Twid3[5 - i];
                dst[dstBase + 5 - i] = ovl * Mp3Tables.Twid3[5 - i] + sum * Mp3Tables.Twid3[2 - i];
            }
        }

        private static void L3ImdctShort(float[] grbuf, int grbufBase, float[] overlap,
            int overlapBase, int nbands, float[] tmp18, float[] co, float[] si)
        {
            for (; nbands > 0; nbands--, overlapBase += 9, grbufBase += 18)
            {
                Array.Copy(grbuf, grbufBase, tmp18, 0, 18);
                Array.Copy(overlap, overlapBase, grbuf, grbufBase, 6);
                L3Imdct12(tmp18, 0, grbuf, grbufBase + 6, overlap, overlapBase + 6, co, si);
                L3Imdct12(tmp18, 1, grbuf, grbufBase + 12, overlap, overlapBase + 6, co, si);
                L3Imdct12(tmp18, 2, overlap, overlapBase, overlap, overlapBase + 6, co, si);
            }
        }

        private static void L3ChangeSign(float[] grbuf, int grbufBase)
        {
            int b = 0;
            int base_ = grbufBase + 18;
            for (; b < 32; b += 2, base_ += 36)
            {
                for (int i = 1; i < 18; i += 2)
                {
                    grbuf[base_ + i] = -grbuf[base_ + i];
                }
            }
        }

        private static void L3ImdctGr(float[] grbuf, int grbufBase, float[] overlap,
            int overlapBase, int blockType, int nLongBands, float[] tmp18, float[] co, float[] si)
        {
            if (nLongBands != 0)
            {
                L3Imdct36(grbuf, grbufBase, overlap, overlapBase, 0, nLongBands, co, si);
                grbufBase += 18 * nLongBands;
                overlapBase += 9 * nLongBands;
            }
            if (blockType == ShortBlockType)
            {
                L3ImdctShort(grbuf, grbufBase, overlap, overlapBase, 32 - nLongBands, tmp18, co, si);
            }
            else
            {
                L3Imdct36(grbuf, grbufBase, overlap, overlapBase,
                    blockType == StopBlockType ? 18 : 0, 32 - nLongBands, co, si);
            }
        }

        // ---------------------------------------------------------------------
        // Bit reservoir and granule decode
        // ---------------------------------------------------------------------

        private void L3SaveReservoir(Bs bs)
        {
            int pos = (bs.Pos + 7) >> 3;
            int remains = (bs.Limit >> 3) - pos;
            if (remains > MaxBitreservoirBytes)
            {
                pos += remains - MaxBitreservoirBytes;
                remains = MaxBitreservoirBytes;
            }
            if (remains > 0)
                Array.Copy(_maindata, pos, _reservBuf, 0, remains);
            _reserv = remains;
        }

        private bool L3RestoreReservoir(Bs bsFrame, Bs sBs, int mainDataBegin)
        {
            int frameBytes = (bsFrame.Limit - bsFrame.Pos) >> 3;
            int bytesHave = Math.Min(_reserv, mainDataBegin);
            int srcOff = Math.Max(0, _reserv - mainDataBegin);
            Array.Copy(_reservBuf, srcOff, _maindata, 0, bytesHave);
            Array.Copy(bsFrame.Buf, bsFrame.Pos >> 3, _maindata, bytesHave, frameBytes);
            BsInit(sBs, _maindata, 0, (bytesHave + frameBytes) * 8);
            return _reserv >= mainDataBegin;
        }

        private void L3Decode(Bs sBs, L3GrInfo[] grInfo, int grBase, int nch)
        {
            for (int ch = 0; ch < nch; ch++)
            {
                int layer3grLimit = sBs.Pos + grInfo[grBase + ch].Part23Length;
                L3DecodeScalefactors(_header, 0, _istPos[ch], sBs, grInfo[grBase + ch], _scf, ch);
                L3Huffman(_grbuf, ch * 576, sBs, grInfo[grBase + ch], _scf, layer3grLimit);
            }

            if (HdrTestIStereo(_header, 0))
            {
                L3IntensityStereo(_grbuf, _istPos[1], grInfo, grBase, _header, 0);
            }
            else if (HdrIsMsStereo(_header, 0))
            {
                L3MidSideStereo(_grbuf, 0, 576);
            }

            for (int ch = 0; ch < nch; ch++)
            {
                L3GrInfo g = grInfo[grBase + ch];
                int aaBands = 31;
                int nLongBands = (g.MixedBlockFlag != 0 ? 2 : 0)
                    << (HdrGetMySampleRate(_header, 0) == 2 ? 1 : 0);

                if (g.NShortSfb != 0)
                {
                    aaBands = nLongBands - 1;
                    L3Reorder(_grbuf, ch * 576 + nLongBands * 18, _syn, g.SfbTab, g.SfbOffset + g.NLongSfb);
                }

                L3Antialias(_grbuf, ch * 576, aaBands);
                L3ImdctGr(_grbuf, ch * 576, _mdctOverlap[ch], 0, g.BlockType, nLongBands, _tmp18, _co, _si);
                L3ChangeSign(_grbuf, ch * 576);
            }
        }

        // ---------------------------------------------------------------------
        // Synthesis filterbank
        // ---------------------------------------------------------------------

        private static void Mp3dDctII(float[] grbuf, int grbufBase, int n, float[] t)
        {
            for (int k = 0; k < n; k++)
            {
                int yBase = grbufBase + k;
                for (int i = 0; i < 8; i++)
                {
                    float x0 = grbuf[yBase + i * 18];
                    float x1 = grbuf[yBase + (15 - i) * 18];
                    float x2 = grbuf[yBase + (16 + i) * 18];
                    float x3 = grbuf[yBase + (31 - i) * 18];
                    float t0 = x0 + x3;
                    float t1 = x1 + x2;
                    float t2 = (x1 - x2) * Mp3Tables.Sec[3 * i + 0];
                    float t3 = (x0 - x3) * Mp3Tables.Sec[3 * i + 1];
                    t[0 * 8 + i] = t0 + t1;
                    t[1 * 8 + i] = (t0 - t1) * Mp3Tables.Sec[3 * i + 2];
                    t[2 * 8 + i] = t3 + t2;
                    t[3 * 8 + i] = (t3 - t2) * Mp3Tables.Sec[3 * i + 2];
                }

                for (int i = 0; i < 4; i++)
                {
                    int x = i * 8;
                    float x0 = t[x + 0], x1 = t[x + 1], x2 = t[x + 2], x3 = t[x + 3],
                          x4 = t[x + 4], x5 = t[x + 5], x6 = t[x + 6], x7 = t[x + 7], xt;
                    xt = x0 - x7; x0 += x7;
                    x7 = x1 - x6; x1 += x6;
                    x6 = x2 - x5; x2 += x5;
                    x5 = x3 - x4; x3 += x4;
                    x4 = x0 - x3; x0 += x3;
                    x3 = x1 - x2; x1 += x2;
                    t[x + 0] = x0 + x1;
                    t[x + 4] = (x0 - x1) * 0.70710677f;
                    x5 = x5 + x6;
                    x6 = (x6 + x7) * 0.70710677f;
                    x7 = x7 + xt;
                    x3 = (x3 + x4) * 0.70710677f;
                    x5 -= x7 * 0.198912367f;
                    x7 += x5 * 0.382683432f;
                    x5 -= x7 * 0.198912367f;
                    x0 = xt - x6; xt += x6;
                    t[x + 1] = (xt + x7) * 0.50979561f;
                    t[x + 2] = (x4 + x3) * 0.54119611f;
                    t[x + 3] = (x0 - x5) * 0.60134488f;
                    t[x + 5] = (x0 + x5) * 0.89997619f;
                    t[x + 6] = (x4 - x3) * 1.30656302f;
                    t[x + 7] = (xt - x7) * 2.56291556f;
                }

                for (int i = 0; i < 7; i++)
                {
                    grbuf[yBase + (4 * i + 0) * 18] = t[0 * 8 + i];
                    grbuf[yBase + (4 * i + 1) * 18] = t[2 * 8 + i] + t[3 * 8 + i] + t[3 * 8 + i + 1];
                    grbuf[yBase + (4 * i + 2) * 18] = t[1 * 8 + i] + t[1 * 8 + i + 1];
                    grbuf[yBase + (4 * i + 3) * 18] = t[2 * 8 + i + 1] + t[3 * 8 + i] + t[3 * 8 + i + 1];
                }
                grbuf[yBase + 28 * 18] = t[0 * 8 + 7];
                grbuf[yBase + 29 * 18] = t[2 * 8 + 7] + t[3 * 8 + 7];
                grbuf[yBase + 30 * 18] = t[1 * 8 + 7];
                grbuf[yBase + 31 * 18] = t[3 * 8 + 7];
            }
        }

        private static short ScalePcm(float sample)
        {
            if (sample >= 32766.5f) return 32767;
            if (sample <= -32767.5f) return -32768;
            short s = (short)(sample + 0.5f);
            if (s < 0) s = (short)(s - 1);
            return s;
        }

        private static void SynthPair(short[] pcm, int pcmBase, int nch, float[] lins, int zBase)
        {
            float a = (lins[zBase + 14 * 64] - lins[zBase]) * 29;
            a += (lins[zBase + 1 * 64] + lins[zBase + 13 * 64]) * 213;
            a += (lins[zBase + 12 * 64] - lins[zBase + 2 * 64]) * 459;
            a += (lins[zBase + 3 * 64] + lins[zBase + 11 * 64]) * 2037;
            a += (lins[zBase + 10 * 64] - lins[zBase + 4 * 64]) * 5153;
            a += (lins[zBase + 5 * 64] + lins[zBase + 9 * 64]) * 6574;
            a += (lins[zBase + 8 * 64] - lins[zBase + 6 * 64]) * 37489;
            a += lins[zBase + 7 * 64] * 75038;
            pcm[pcmBase] = ScalePcm(a);

            zBase += 2;
            a = lins[zBase + 14 * 64] * 104;
            a += lins[zBase + 12 * 64] * 1567;
            a += lins[zBase + 10 * 64] * 9727;
            a += lins[zBase + 8 * 64] * 64019;
            a += lins[zBase + 6 * 64] * -9975;
            a += lins[zBase + 4 * 64] * -45;
            a += lins[zBase + 2 * 64] * 146;
            a += lins[zBase] * -5;
            pcm[pcmBase + 16 * nch] = ScalePcm(a);
        }

        private static void Mp3dSynth(float[] xl, int xlBase, short[] dstl, int dstlBase,
            int nch, float[] lins, int linsBase, float[] a, float[] b)
        {
            int xrBase = xlBase + 576 * (nch - 1);
            int dstrBase = dstlBase + (nch - 1);
            int zlin = linsBase + 15 * 64;
            int wIdx = 0;

            lins[zlin + 4 * 15] = xl[xlBase + 18 * 16];
            lins[zlin + 4 * 15 + 1] = xl[xrBase + 18 * 16];
            lins[zlin + 4 * 15 + 2] = xl[xlBase];
            lins[zlin + 4 * 15 + 3] = xl[xrBase];

            lins[zlin + 4 * 31] = xl[xlBase + 1 + 18 * 16];
            lins[zlin + 4 * 31 + 1] = xl[xrBase + 1 + 18 * 16];
            lins[zlin + 4 * 31 + 2] = xl[xlBase + 1];
            lins[zlin + 4 * 31 + 3] = xl[xrBase + 1];

            SynthPair(dstl, dstrBase, nch, lins, linsBase + 4 * 15 + 1);
            SynthPair(dstl, dstrBase + 32 * nch, nch, lins, linsBase + 4 * 15 + 64 + 1);
            SynthPair(dstl, dstlBase, nch, lins, linsBase + 4 * 15);
            SynthPair(dstl, dstlBase + 32 * nch, nch, lins, linsBase + 4 * 15 + 64);

            for (int i = 14; i >= 0; i--)
            {
                lins[zlin + 4 * i] = xl[xlBase + 18 * (31 - i)];
                lins[zlin + 4 * i + 1] = xl[xrBase + 18 * (31 - i)];
                lins[zlin + 4 * i + 2] = xl[xlBase + 1 + 18 * (31 - i)];
                lins[zlin + 4 * i + 3] = xl[xrBase + 1 + 18 * (31 - i)];
                lins[zlin + 4 * (i + 16)] = xl[xlBase + 1 + 18 * (1 + i)];
                lins[zlin + 4 * (i + 16) + 1] = xl[xrBase + 1 + 18 * (1 + i)];
                lins[zlin + 4 * (i - 16) + 2] = xl[xlBase + 18 * (1 + i)];
                lins[zlin + 4 * (i - 16) + 3] = xl[xrBase + 18 * (1 + i)];

                for (int j = 0; j < 4; j++) { a[j] = 0f; b[j] = 0f; }
                for (int k = 0; k < 8; k++)
                {
                    float w0 = Mp3Tables.Win[wIdx++];
                    float w1 = Mp3Tables.Win[wIdx++];
                    int vz = zlin + 4 * i - k * 64;
                    int vy = zlin + 4 * i - (15 - k) * 64;
                    bool s2 = (k & 1) != 0;
                    for (int j = 0; j < 4; j++)
                    {
                        float v0 = lins[vz + j];
                        float v1 = lins[vy + j];
                        b[j] += v0 * w1 + v1 * w0;
                        a[j] += s2 ? (v1 * w1 - v0 * w0) : (v0 * w0 - v1 * w1);
                    }
                }

                dstl[dstrBase + (15 - i) * nch] = ScalePcm(a[1]);
                dstl[dstrBase + (17 + i) * nch] = ScalePcm(b[1]);
                dstl[dstlBase + (15 - i) * nch] = ScalePcm(a[0]);
                dstl[dstlBase + (17 + i) * nch] = ScalePcm(b[0]);
                dstl[dstrBase + (47 - i) * nch] = ScalePcm(a[3]);
                dstl[dstrBase + (49 + i) * nch] = ScalePcm(b[3]);
                dstl[dstlBase + (47 - i) * nch] = ScalePcm(a[2]);
                dstl[dstlBase + (49 + i) * nch] = ScalePcm(b[2]);
            }
        }

        private void Mp3dSynthGranule(float[] grbuf, int grbufBase, int nbands, int nch,
            short[] pcm, int pcmBase, float[] lins, int linsBase)
        {
            for (int i = 0; i < nch; i++)
            {
                Mp3dDctII(grbuf, grbufBase + 576 * i, nbands, _t32);
            }

            Array.Copy(_qmfState, 0, lins, linsBase, 15 * 64);

            for (int i = 0; i < nbands; i += 2)
            {
                Mp3dSynth(grbuf, grbufBase + i, pcm, pcmBase + 32 * nch * i, nch,
                    lins, linsBase + i * 64, _a4, _b4);
            }

            if (nch == 1)
            {
                for (int i = 0; i < 15 * 64; i += 2)
                {
                    _qmfState[i] = lins[linsBase + nbands * 64 + i];
                }
            }
            else
            {
                Array.Copy(lins, linsBase + nbands * 64, _qmfState, 0, 15 * 64);
            }
        }

        // ---------------------------------------------------------------------
        // Frame synchronization and top-level decode
        // ---------------------------------------------------------------------

        private static bool Mp3dMatchFrame(byte[] mp3, int baseOff, int mp3Bytes, int frameBytes)
        {
            int i = 0, nmatch;
            for (nmatch = 0; nmatch < MaxFrameSyncMatches; nmatch++)
            {
                i += HdrFrameBytes(mp3, baseOff + i, frameBytes) + HdrPadding(mp3, baseOff + i);
                if (i + HdrSize > mp3Bytes)
                    return nmatch > 0;
                if (!HdrCompare(mp3, baseOff, mp3, baseOff + i))
                    return false;
            }
            return true;
        }

        private static int Mp3dFindFrame(byte[] mp3, int mp3Bytes, ref int freeFormatBytes,
            out int frameBytes)
        {
            for (int i = 0; i < mp3Bytes - HdrSize; i++)
            {
                if (HdrValid(mp3, i))
                {
                    int fb = HdrFrameBytes(mp3, i, freeFormatBytes);
                    int frameAndPadding = fb + HdrPadding(mp3, i);

                    for (int k = HdrSize; fb == 0 && k < MaxFreeFormatFrameSize && i + 2 * k < mp3Bytes - HdrSize; k++)
                    {
                        if (HdrCompare(mp3, i, mp3, i + k))
                        {
                            int fb2 = k - HdrPadding(mp3, i);
                            int nextfb = fb2 + HdrPadding(mp3, i + k);
                            if (i + k + nextfb + HdrSize > mp3Bytes || !HdrCompare(mp3, i, mp3, i + k + nextfb))
                                continue;
                            frameAndPadding = k;
                            fb = fb2;
                            freeFormatBytes = fb2;
                        }
                    }

                    // Valid header whose frame is not yet fully present: skip any
                    // garbage before it (advance to i) and wait for the rest of the
                    // frame, instead of dropping the whole buffer.
                    if (fb != 0 && i + frameAndPadding > mp3Bytes)
                    {
                        frameBytes = frameAndPadding;
                        return i;
                    }

                    // Accept a frame when it validates against the following frames, or
                    // when it sits at the buffer start and there simply isn't a full next
                    // header yet to validate (i.e. the frame itself is complete).
                    bool validated = fb != 0 && i + frameAndPadding <= mp3Bytes &&
                                     Mp3dMatchFrame(mp3, i, mp3Bytes - i, fb);
                    bool completeAtStart = i == 0 && fb != 0 && frameAndPadding <= mp3Bytes &&
                                           i + frameAndPadding + HdrSize > mp3Bytes;
                    if (validated || completeAtStart)
                    {
                        frameBytes = frameAndPadding;
                        return i;
                    }
                    freeFormatBytes = 0;
                }
            }
            frameBytes = 0;
            return mp3Bytes;
        }

        private int DecodeFrame(byte[] mp3, int mp3Bytes, short[] pcm, FrameInfo info)
        {
            int i = 0, igr, frameSize = 0, success = 1;

            if (mp3Bytes > 4 && _header[0] == 0xFF && HdrCompare(_header, 0, mp3, 0))
            {
                frameSize = HdrFrameBytes(mp3, 0, _freeFormatBytes) + HdrPadding(mp3, 0);
                if (frameSize > mp3Bytes)
                {
                    // Known frame boundary but the frame is incomplete: wait for more
                    // data instead of resyncing (which would drop this frame).
                    info.FrameBytes = 0;
                    return 0;
                }
                if (frameSize != mp3Bytes && frameSize + HdrSize <= mp3Bytes && !HdrCompare(mp3, 0, mp3, frameSize))
                {
                    // Next frame header is present but mismatched: corruption -> resync.
                    frameSize = 0;
                }
            }

            if (frameSize == 0)
            {
                ClearDecoderState();
                i = Mp3dFindFrame(mp3, mp3Bytes, ref _freeFormatBytes, out frameSize);
                if (frameSize == 0 || i + frameSize > mp3Bytes)
                {
                    info.FrameBytes = i;
                    return 0;
                }
            }

            Array.Copy(mp3, i, _header, 0, HdrSize);
            info.FrameBytes = i + frameSize;
            info.FrameOffset = i;
            info.Channels = HdrIsMono(mp3, i) ? 1 : 2;
            info.Hz = HdrSampleRateHz(mp3, i);
            info.Layer = 4 - HdrGetLayer(mp3, i);
            info.BitrateKbps = HdrBitrateKbps(mp3, i);

            if (info.Layer != 3)
            {
                // Layer I/II unsupported: skip the frame.
                return 0;
            }

            BsInit(_bsFrame, mp3, (i + HdrSize) * 8, (i + frameSize) * 8);
            if (HdrIsCrc(mp3, i))
            {
                GetBits(_bsFrame, 16);
            }

            int mainDataBegin = L3ReadSideInfo(_bsFrame, _grInfo, mp3, i);
            if (mainDataBegin < 0 || _bsFrame.Pos > _bsFrame.Limit)
            {
                ClearDecoderState();
                return 0;
            }

            success = L3RestoreReservoir(_bsFrame, _sBs, mainDataBegin) ? 1 : 0;
            if (success != 0)
            {
                int granules = HdrTestMpeg1(mp3, i) ? 2 : 1;
                for (igr = 0; igr < granules; igr++)
                {
                    int pcmOff = igr * 576 * info.Channels;
                    Array.Clear(_grbuf, 0, _grbuf.Length);
                    L3Decode(_sBs, _grInfo, igr * info.Channels, info.Channels);
                    Mp3dSynthGranule(_grbuf, 0, 18, info.Channels, pcm, pcmOff, _syn, 0);
                }
            }
            L3SaveReservoir(_sBs);

            return success * HdrFrameSamples(mp3, i);
        }
    }

    // -------------------------------------------------------------------------
    // Helper types
    // -------------------------------------------------------------------------

    internal sealed class Bs
    {
        public byte[] Buf;
        public int Pos;   // bit position
        public int Limit; // bit limit
    }

    internal sealed class L3GrInfo
    {
        public byte[] SfbTab;
        public int SfbOffset;
        public ushort Part23Length;
        public ushort BigValues;
        public ushort ScalefacCompress;
        public byte GlobalGain;
        public byte BlockType;
        public byte MixedBlockFlag;
        public byte NLongSfb;
        public byte NShortSfb;
        public readonly byte[] TableSelect = new byte[3];
        public readonly byte[] RegionCount = new byte[3];
        public readonly byte[] SubblockGain = new byte[3];
        public byte Preflag;
        public byte ScalefacScale;
        public byte Count1Table;
        public byte Scfsi;
    }

    internal sealed class FrameInfo
    {
        public int FrameBytes;
        public int FrameOffset;
        public int Channels;
        public int Hz;
        public int Layer;
        public int BitrateKbps;
    }
}
