using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InternetRadio;

namespace InternetRadio.Tests
{
    /// <summary>
    /// Test/validation harness.
    ///   decode &lt;input.mp3&gt; &lt;output.pcm&gt;   — decode a pure MP3 file to raw 16-bit PCM.
    ///   live   &lt;url&gt; &lt;seconds&gt; &lt;output.wav&gt; — stream a station for N seconds to a WAV file.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage:");
                Console.WriteLine("  decode  <input.mp3> <output.pcm>");
                Console.WriteLine("  chunk   <input.mp3> <output.pcm> <chunkBytes>");
                Console.WriteLine("  live    <url> <seconds> <output.wav>");
                Console.WriteLine("  play    <url> <seconds>            (Windows-only, live playback)");
                Console.WriteLine("  wavplay <file.wav> [seconds]       (Windows-only, play a local WAV)");
                Console.WriteLine("  bench   <input.mp3> [iterations] [chunkBytes]");
                return 2;
            }

            switch (args[0])
            {
                case "decode":
                    return DecodeFile(args[1], args[2]);
                case "chunk":
                    return DecodeFileChunked(args[1], args[2], int.Parse(args[3]));
                case "live":
                    return Live(args[1], int.Parse(args[2]), args[3]);
                case "play":
                    return Play(args[1], int.Parse(args[2]));
                case "wavplay":
                    return WavPlay(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
                case "bench":
                    return Bench(args[1],
                        args.Length > 2 ? int.Parse(args[2]) : 5,
                        args.Length > 3 ? int.Parse(args[3]) : 0);
                default:
                    Console.WriteLine("unknown command: " + args[0]);
                    return 2;
            }
        }

        private static int DecodeFile(string input, string output)
        {
            byte[] bytes = File.ReadAllBytes(input);
            var decoder = new Mp3Decoder();
            int totalSamples = 0, hz = 0, ch = 0;

            using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
            {
                decoder.PcmDecoded += f =>
                {
                    hz = f.SampleRate;
                    ch = f.Channels;
                    byte[] pcm = new byte[f.Samples.Length * 2];
                    Buffer.BlockCopy(f.Samples, 0, pcm, 0, pcm.Length);
                    fs.Write(pcm, 0, pcm.Length);
                    totalSamples += f.Samples.Length;
                };
                decoder.Feed(bytes, 0, bytes.Length);
            }

            Console.WriteLine($"decoded {totalSamples} interleaved samples, {hz} Hz, {ch} ch");
            return 0;
        }

        /// <summary>Decodes an MP3 file feeding the decoder in small chunks, to verify the
        /// streaming/resync path does not drop or duplicate frames regardless of chunking.</summary>
        private static int DecodeFileChunked(string input, string output, int chunkSize)
        {
            byte[] bytes = File.ReadAllBytes(input);
            var decoder = new Mp3Decoder();
            int totalSamples = 0, hz = 0, ch = 0;

            using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
            {
                decoder.PcmDecoded += f =>
                {
                    hz = f.SampleRate;
                    ch = f.Channels;
                    byte[] pcm = new byte[f.Samples.Length * 2];
                    Buffer.BlockCopy(f.Samples, 0, pcm, 0, pcm.Length);
                    fs.Write(pcm, 0, pcm.Length);
                    totalSamples += f.Samples.Length;
                };

                int pos = 0;
                while (pos < bytes.Length)
                {
                    int n = Math.Min(chunkSize, bytes.Length - pos);
                    decoder.Feed(bytes, pos, n);
                    pos += n;
                }
            }

            Console.WriteLine($"chunked({chunkSize} B): decoded {totalSamples} interleaved samples, {hz} Hz, {ch} ch");
            return 0;
        }

        private static int Live(string url, int seconds, string output)
        {
            var radio = new InternetRadio
            {
                PrebufferBytes = 64 * 1024,
            };
            radio.SetUrl(url);

            int hz = 0, ch = 0;
            using (var ms = new MemoryStream())
            {
                radio.PcmDecoded += f =>
                {
                    hz = f.SampleRate;
                    ch = f.Channels;
                    byte[] pcm = new byte[f.Samples.Length * 2];
                    Buffer.BlockCopy(f.Samples, 0, pcm, 0, pcm.Length);
                    ms.Write(pcm, 0, pcm.Length);
                };
                radio.StreamTitleChanged += t => Console.WriteLine("TITLE: " + t);
                radio.StateChanged += s => Console.WriteLine("STATE: " + s);
                radio.Error += e => Console.WriteLine("ERROR: " + e.GetType().Name + ": " + e.Message);

                radio.Start();
                Thread.Sleep(seconds * 1000);
                radio.Stop();

                Console.WriteLine($"streamed {ms.Length / 2} interleaved samples, {hz} Hz, {ch} ch");
                WriteWav(output, ms.ToArray(), hz, ch);
            }

            return 0;
        }

        private static int Play(string url, int seconds)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("The 'play' command is Windows-only.");
                return 2;
            }

            // Moderate prebuffer: more than the minimal startup, without a long wait.
            int prebufferBytes = 128 * 1024; // ~8 s of audio at 128 kbps
            var radio = new InternetRadio
            {
                PrebufferBytes = prebufferBytes,
                RingCapacityBytes = 1024 * 1024,
                SocketReceiveBufferBytes = 1024 * 1024,
            };
            radio.SetUrl(url);

            var player = new WaveOutPlayer();
            bool opened = false;
            int frames = 0;
            long firstFrameAt = 0;

            radio.PcmDecoded += frame =>
            {
                if (!opened)
                {
                    player.Open(frame.SampleRate, frame.Channels);
                    opened = true;
                }
                if (frames == 0)
                    firstFrameAt = Environment.TickCount64;
                player.Write(frame.Samples, 0, frame.Samples.Length);
                frames++;
            };
            radio.StreamTitleChanged += t => Console.WriteLine("TITLE: " + t);
            radio.StateChanged += s => Console.WriteLine("STATE: " + s);
            radio.Error += e => Console.WriteLine("ERROR: " + e.GetType().Name + ": " + e.Message);

            Console.WriteLine("Prebuffer: " + (prebufferBytes / 1024) + " KB (~" + (prebufferBytes / 16384) + " s at 128 kbps). Playing for " + seconds + " seconds...");
            Console.WriteLine("  sec | frames | recv KB/s | buff KB | out queue | min queue");
            radio.Start();

            long prevBytes = 0;
            for (int i = 1; i <= seconds; i++)
            {
                Thread.Sleep(1000);
                long total = radio.BytesReceived;
                double kbps = (total - prevBytes) / 1024.0;
                prevBytes = total;
                Console.WriteLine(
                    "  " + i.ToString().PadLeft(3) + " | " +
                    frames.ToString().PadLeft(6) + " | " +
                    kbps.ToString("0.0").PadLeft(9) + " | " +
                    (radio.BufferedBytes / 1024).ToString().PadLeft(6) + " | " +
                    player.InFlightCount.ToString().PadLeft(9) + " | " +
                    player.MinInFlight.ToString().PadLeft(9));
            }
            radio.Stop();
            player.Dispose();

            double playedSec = (Environment.TickCount64 - firstFrameAt) / 1000.0;
            double expected = Math.Max(0, playedSec) * 44100.0 / 1152.0;
            double ratio = expected > 0 ? frames / expected : 0;
            Console.WriteLine("Done. Total frames: " + frames + " (played " + playedSec.ToString("0.0") + " s, expected ~" + (int)expected + " at real-time, " + (ratio * 100.0).ToString("0.0") + "%)");
            Console.WriteLine("WaveOut queue: min depth " + player.MinInFlight + " / " + WaveOutPlayer.BufferCount + " blocks" +
                (player.MinInFlight <= 1 ? " -> UNDERUN(S) detected (queue drained)" : "") + ".");
            if (ratio < 0.98)
                Console.WriteLine("Source delivered less than real-time: the radio server is streaming slower than playback speed.");
            return 0;
        }

        /// <summary>
        /// Plays a local 16-bit PCM WAV through WaveOutPlayer. This isolates the playback
        /// layer from the network + decoder: the source is perfectly clean and perfectly
        /// timed, so any skips here are in WaveOutPlayer/driver, not in streaming.
        /// </summary>
        private static int WavPlay(string path, int seconds)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("The 'wavplay' command is Windows-only.");
                return 2;
            }

            var (samples, hz, ch) = ReadWav(path);
            int totalSamples = samples.Length;
            if (seconds > 0)
                totalSamples = Math.Min(totalSamples, seconds * hz * ch);

            var player = new WaveOutPlayer();
            player.Open(hz, ch);

            Console.WriteLine("Playing " + path + ": " + (samples.Length / ch) + " frames, " + hz + " Hz, " + ch + " ch");
            Console.WriteLine("  sec | fed    | out queue | min queue | devPos ms | wall ms | drift ms");

            int fedFrames = 0;
            int pos = 0;
            int chunkFrames = 1152; // one MP3-frame-equivalent (~26 ms)
            int secIdx = 0;
            long startWall = Environment.TickCount64;
            long nextReport = startWall + 1000;

            // Feed as fast as the queue allows: Write() blocks once the device queue
            // is full, so steady-state is identical to live playback (queue full,
            // replenished on completion).
            while (pos < totalSamples)
            {
                int n = Math.Min(chunkFrames * ch, totalSamples - pos);
                player.Write(samples, pos, n);
                pos += n;
                fedFrames += n / ch;

                long now = Environment.TickCount64;
                if (now >= nextReport)
                {
                    secIdx++;
                    nextReport = now + 1000;
                    ReportPlayback(secIdx, fedFrames, player, now - startWall);
                }
            }

            // Let the tail (up to ~2.4 s of queued blocks) drain.
            while (player.InFlightCount > 0)
            {
                Thread.Sleep(500);
                long now = Environment.TickCount64;
                if (now >= nextReport)
                {
                    secIdx++;
                    nextReport = now + 1000;
                    ReportPlayback(secIdx, fedFrames, player, now - startWall);
                }
            }

            player.Dispose();
            Console.WriteLine("Done. Fed " + fedFrames + " frames; min queue depth " + player.MinInFlight + " / " + WaveOutPlayer.BufferCount + ".");
            return 0;
        }

        private static void ReportPlayback(int sec, int fedFrames, WaveOutPlayer player, long wallMs)
        {
            long dev = player.PositionMs;
            Console.WriteLine(
                "  " + sec.ToString().PadLeft(3) + " | " +
                fedFrames.ToString().PadLeft(6) + " | " +
                player.InFlightCount.ToString().PadLeft(9) + " | " +
                player.MinInFlight.ToString().PadLeft(9) + " | " +
                (dev >= 0 ? dev.ToString() : "-").PadLeft(9) + " | " +
                wallMs.ToString().PadLeft(7) + " | " +
                (dev >= 0 ? (dev - wallMs).ToString() : "-").PadLeft(8));
        }

        /// <summary>Minimal 16-bit PCM WAV reader (RIFF: fmt + data chunks).</summary>
        private static (short[] samples, int sampleRate, int channels) ReadWav(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            if (all.Length < 44 || Encoding.ASCII.GetString(all, 0, 4) != "RIFF")
                throw new InvalidDataException("Not a RIFF/WAV file.");

            int hz = 0, ch = 0, bits = 0;
            int dataOff = -1, dataLen = 0;
            int p = 12;
            while (p + 8 <= all.Length)
            {
                string id = Encoding.ASCII.GetString(all, p, 4);
                int size = BitConverter.ToInt32(all, p + 4);
                int body = p + 8;
                if (body + size > all.Length)
                    break;

                if (id == "fmt ")
                {
                    ch = BitConverter.ToInt16(all, body + 2);
                    hz = BitConverter.ToInt32(all, body + 4);
                    bits = BitConverter.ToInt16(all, body + 14);
                }
                else if (id == "data")
                {
                    dataOff = body;
                    dataLen = size;
                }

                p = body + size + (size & 1); // chunks are word-aligned
            }

            if (dataOff < 0 || bits != 16 || hz <= 0 || ch <= 0)
                throw new InvalidDataException("Only 16-bit PCM WAV is supported.");

            short[] samples = new short[dataLen / 2];
            Buffer.BlockCopy(all, dataOff, samples, 0, dataLen);
            return (samples, hz, ch);
        }

        private static void WriteWav(string path, byte[] pcm16, int sampleRate, int channels)
        {
            if (sampleRate <= 0) sampleRate = 44100;
            if (channels <= 0) channels = 2;

            int byteRate = sampleRate * channels * 2;
            int dataLen = pcm16.Length - (pcm16.Length % 2);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataLen);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);                 // PCM fmt chunk size
                w.Write((short)1);           // PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(byteRate);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataLen);
                w.Write(pcm16, 0, dataLen);
            }
        }

        // ---------------------------------------------------------------------
        // Decoder benchmark
        // ---------------------------------------------------------------------

        private static long _benchSamples;
        private static int _benchHz;
        private static int _benchCh;

        /// <summary>
        /// Benchmarks the MP3 decoder on a real file. The file is read once; it is then
        /// decoded repeatedly (whole-file or in streaming-sized chunks) so timing covers
        /// only the decoder, not I/O. Reports throughput, real-time factor and managed
        /// allocations per pass.
        /// </summary>
        private static int Bench(string input, int iterations, int chunkBytes)
        {
            if (iterations <= 0) iterations = 5;

            byte[] bytes = File.ReadAllBytes(input);
            var decoder = new Mp3Decoder();

            // The handler references only static fields, so the compiler caches the
            // delegate: the benchmark itself does not allocate per decoded frame.
            decoder.PcmDecoded += f =>
            {
                _benchSamples += f.Samples.Length;
                _benchHz = f.SampleRate;
                _benchCh = f.Channels;
            };

            // Warm-up: JIT and let the decoder's reusable buffers reach steady size.
            _benchSamples = 0;
            FeedBench(decoder, bytes, chunkBytes);
            long samplesPerPass = _benchSamples;
            int hz = _benchHz;
            int ch = _benchCh;

            if (samplesPerPass <= 0 || hz <= 0 || ch <= 0)
            {
                Console.WriteLine("No PCM decoded; is this a valid Layer III MP3?");
                return 2;
            }

            double audioSeconds = samplesPerPass / (double)(hz * ch);
            double inputMB = bytes.Length / (1024.0 * 1024.0);

            var elapsedMs = new double[iterations];
            var allocBytes = new long[iterations];

            for (int i = 0; i < iterations; i++)
            {
                decoder.Reset();
                _benchSamples = 0;
                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                FeedBench(decoder, bytes, chunkBytes);
                sw.Stop();
                elapsedMs[i] = sw.Elapsed.TotalMilliseconds;
                allocBytes[i] = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            }

            Array.Sort(elapsedMs);
            Array.Sort(allocBytes);

            double minMs = elapsedMs[0];
            double medMs = Median(elapsedMs);
            double maxMs = elapsedMs[elapsedMs.Length - 1];

            Console.WriteLine("benchmark: " + input);
            Console.WriteLine("  input      : " + inputMB.ToString("0.00") + " MiB, " +
                              audioSeconds.ToString("0.00") + " s audio (" + hz + " Hz, " + ch + " ch)");
            Console.WriteLine("  mode       : " + (chunkBytes > 0 ? "chunked " + chunkBytes + " B" : "whole file"));
            Console.WriteLine("  passes     : " + iterations);
            Console.WriteLine("  time       : min " + minMs.ToString("0.0") + " ms | median " +
                              medMs.ToString("0.0") + " ms | max " + maxMs.ToString("0.0") + " ms");
            Console.WriteLine("  throughput : " + (inputMB / (medMs / 1000.0)).ToString("0.00") + " MiB/s (median)");
            Console.WriteLine("  real-time  : x" + (audioSeconds / (medMs / 1000.0)).ToString("0.0") + " (median)");
            Console.WriteLine("  alloc/pass : min " + allocBytes[0] + " B | median " + Median(allocBytes) +
                              " B | max " + allocBytes[allocBytes.Length - 1] + " B");

            return 0;
        }

        private static void FeedBench(Mp3Decoder decoder, byte[] bytes, int chunkBytes)
        {
            if (chunkBytes <= 0)
            {
                decoder.Feed(bytes, 0, bytes.Length);
                return;
            }

            int pos = 0;
            while (pos < bytes.Length)
            {
                int n = Math.Min(chunkBytes, bytes.Length - pos);
                decoder.Feed(bytes, pos, n);
                pos += n;
            }
        }

        private static double Median(double[] xs)
        {
            int n = xs.Length;
            return n % 2 == 0 ? (xs[n / 2 - 1] + xs[n / 2]) / 2.0 : xs[n / 2];
        }

        private static long Median(long[] xs)
        {
            int n = xs.Length;
            return n % 2 == 0 ? (xs[n / 2 - 1] + xs[n / 2]) / 2 : xs[n / 2];
        }
    }
}
