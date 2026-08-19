using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InternetRadio
{
    /// <summary>
    /// Minimal raw HTTP/ICY client for streaming endpoints. Establishes a TCP (or TLS)
    /// connection, sends a hand-crafted request, parses the response status line and
    /// headers, then exposes the endless audio body as a stream-like read source.
    /// No HttpClient dependency: this keeps behavior identical across runtimes and Unity.
    /// </summary>
    internal sealed class StreamClient : IDisposable
    {
        private const int HeaderBufferSize = 8192;

        private TcpClient _tcp;
        private Stream _stream;
        private readonly byte[] _one = new byte[1];
        private readonly Dictionary<string, string> _headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public StationInfo Station { get; } = new StationInfo();
        public string StatusLine { get; private set; }
        public int StatusCode { get; private set; }

        public static StreamClient Connect(string url, int connectTimeoutMs, int readTimeoutMs, int receiveBufferSize)
        {
            string currentUrl = url;
            for (int hop = 0; hop < 10; hop++)
            {
                var u = ParseUrl(currentUrl);

                var client = new StreamClient();
                client._tcp = new TcpClient();
                client._tcp.NoDelay = true;
                // A large SO_RCVBUF lets the TCP stack absorb the server's bursty
                // delivery (several hundred KB) without blocking the sender and
                // causing gaps. Default is only a few tens of KB.
                if (receiveBufferSize > 0)
                    client._tcp.ReceiveBufferSize = receiveBufferSize;

                IAsyncResult ar;
                try
                {
                    ar = client._tcp.BeginConnect(u.Host, u.Port, null, null);
                }
                catch (Exception ex)
                {
                    client._tcp = null;
                    throw new InvalidOperationException("Cannot start connection to " + u.Host + ":" + u.Port, ex);
                }

                if (!ar.AsyncWaitHandle.WaitOne(connectTimeoutMs))
                {
                    try { client._tcp.Close(); } catch { /* ignore */ }
                    throw new TimeoutException("Connection to " + u.Host + ":" + u.Port + " timed out.");
                }

                try
                {
                    client._tcp.EndConnect(ar);
                }
                catch (Exception ex)
                {
                    try { client._tcp.Close(); } catch { /* ignore */ }
                    throw new IOException("Cannot connect to " + u.Host + ":" + u.Port + ": " + ex.Message, ex);
                }

                client._tcp.ReceiveTimeout = readTimeoutMs;
                client._tcp.SendTimeout = readTimeoutMs;

                client._stream = client._tcp.GetStream();

                if (u.Scheme == "https")
                {
                    var ssl = new SslStream(client._stream, false);
                    ssl.AuthenticateAsClient(u.Host);
                    client._stream = ssl;
                }

                string request =
                    "GET " + u.PathAndQuery + " HTTP/1.0\r\n" +
                    "Host: " + u.Host + "\r\n" +
                    "User-Agent: InternetRadio/1.0\r\n" +
                    "Icy-MetaData: 1\r\n" +
                    "Accept: */*\r\n" +
                    "Connection: close\r\n" +
                    "\r\n";

                byte[] req = Encoding.ASCII.GetBytes(request);
                client._stream.Write(req, 0, req.Length);
                client._stream.Flush();

                client.ReadHeaders();

                if (client.StatusCode >= 300 && client.StatusCode < 400 &&
                    client._headers.TryGetValue("Location", out string location))
                {
                    client.Dispose();
                    currentUrl = ResolveUrl(currentUrl, location);
                    continue;
                }

                return client;
            }

            throw new IOException("Too many redirects while connecting to " + url + ".");
        }

        private static string ResolveUrl(string baseUrl, string location)
        {
            if (string.IsNullOrEmpty(location))
                return baseUrl;

            if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return location;

            var b = ParseUrl(baseUrl);
            string path = location.StartsWith("/", StringComparison.Ordinal) ? location : "/" + location;
            return b.Scheme + "://" + b.Host + ":" + b.Port + path;
        }

        /// <summary>Reads raw body bytes (audio interleaved with in-band metadata).</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            return _stream.Read(buffer, offset, count);
        }

        private void ReadHeaders()
        {
            var statusLine = ReadLine();
            if (statusLine == null)
                throw new EndOfStreamException("Server closed the connection before sending a status line.");

            StatusLine = statusLine;
            ParseStatus(statusLine);

            while (true)
            {
                string line = ReadLine();
                if (line == null)
                    throw new EndOfStreamException("Server closed the connection while sending headers.");
                if (line.Length == 0)
                    break;

                int idx = line.IndexOf(':');
                if (idx <= 0)
                    continue;

                string name = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                _headers[name] = value;
                ApplyHeader(name, value);
            }
        }

        private void ParseStatus(string statusLine)
        {
            // "ICY 200 OK" or "HTTP/1.0 200 OK". 3xx redirects are followed by Connect.
            var parts = statusLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            StatusCode = parts.Length > 1 && int.TryParse(parts[1], out int code) ? code : 0;
            if (StatusCode < 200 || StatusCode >= 400)
                throw new IOException("Streaming server returned: " + statusLine);
        }

        private void ApplyHeader(string name, string value)
        {
            switch (name.ToLowerInvariant())
            {
                case "icy-name": Station.Name = value; break;
                case "icy-genre": Station.Genre = value; break;
                case "icy-url": Station.Url = value; break;
                case "icy-br": if (int.TryParse(value, out int br)) Station.BitrateKbps = br; break;
                case "icy-sr": if (int.TryParse(value, out int sr)) Station.SampleRate = sr; break;
                case "content-type": Station.ContentType = value; break;
                case "icy-metaint": if (int.TryParse(value, out int mi)) Station.MetadataInterval = mi; break;
            }
        }

        private string ReadLine()
        {
            // Reads up to '\n', strips trailing '\r'. Returns null on clean EOF at line start.
            var sb = new StringBuilder(64);
            int read = _stream.Read(_one, 0, 1);
            if (read == 0)
                return null;

            while (true)
            {
                byte b = _one[0];
                if (b == (byte)'\n')
                    break;
                if (b != (byte)'\r')
                    sb.Append((char)b);
                if (sb.Length > HeaderBufferSize)
                    throw new InvalidDataException("Response header line is too long.");
                if (_stream.Read(_one, 0, 1) == 0)
                    break;
            }

            return sb.ToString();
        }

        private static (string Scheme, string Host, int Port, string PathAndQuery) ParseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Station URL is empty.", nameof(url));

            string s = url.Trim();
            string scheme = "http";

            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "http";
                s = s.Substring("http://".Length);
            }
            else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "https";
                s = s.Substring("https://".Length);
            }

            if (s.Length == 0)
                throw new ArgumentException("Station URL has no host.", nameof(url));

            int slash = s.IndexOf('/');
            string hostPort = slash >= 0 ? s.Substring(0, slash) : s;
            string pathAndQuery = slash >= 0 ? s.Substring(slash) : "/";

            string host;
            int port = scheme == "https" ? 443 : 80;

            if (hostPort.StartsWith("[", StringComparison.Ordinal))
            {
                // IPv6 literal: [::1]:8000
                int close = hostPort.IndexOf(']');
                if (close < 0)
                    throw new ArgumentException("Invalid IPv6 host in URL.", nameof(url));
                host = hostPort.Substring(1, close - 1);
                string rest = hostPort.Substring(close + 1);
                if (rest.StartsWith(":", StringComparison.Ordinal))
                {
                    if (!int.TryParse(rest.Substring(1), out port))
                        throw new ArgumentException("Invalid port in URL.", nameof(url));
                }
            }
            else
            {
                int colon = hostPort.LastIndexOf(':');
                if (colon > 0 && hostPort.IndexOf(':') == colon)
                {
                    host = hostPort.Substring(0, colon);
                    if (!int.TryParse(hostPort.Substring(colon + 1), out port))
                        throw new ArgumentException("Invalid port in URL.", nameof(url));
                }
                else
                {
                    host = hostPort;
                }
            }

            if (host.Length == 0)
                throw new ArgumentException("Station URL has no host.", nameof(url));

            return (scheme, host, port, pathAndQuery);
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _tcp?.Close(); } catch { /* ignore */ }
        }
    }
}
