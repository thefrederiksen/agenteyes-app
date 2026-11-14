using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentEyes.Headless;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    /// <summary>
    /// Issue #123: a POST with no body and no Content-Length (curl -X POST url, no -d) must be rejected
    /// BEFORE it has any effect, and the response and GET /status must agree. The requests are written
    /// on a raw socket so the framing is exactly what the test says - an HTTP library would add
    /// "Content-Length: 0" on its own and the defect would never be exercised.
    ///
    /// What these tests can and cannot see: they drive the real HeadlessControlServer over loopback,
    /// with an idle RecordingService. They never start a real recording (that needs an X display and
    /// ffmpeg), so "the handler did not run" is proved by the server's dispatch counter and by the 409
    /// a dispatched stop gives while idle - not by a recording surviving. The live stop-while-recording
    /// case is driven through the installed service and recorded in docs/cencon/proof/issue-123/.
    /// </summary>
    public sealed class HeadlessControlServerFramingTests : IDisposable
    {
        private readonly HeadlessControlServer _server;

        public HeadlessControlServerFramingTests()
        {
            _server = new HeadlessControlServer(new RecordingService(), FreePort());
            _server.Start();
        }

        public void Dispose() => _server.Dispose();

        [Theory]
        [InlineData("POST", null, null, true)]
        [InlineData("post", null, null, true)]
        [InlineData("PUT", null, null, true)]
        [InlineData("POST", "", null, true)]
        [InlineData("POST", "0", null, false)]
        [InlineData("POST", "2", null, false)]
        [InlineData("POST", null, "chunked", false)]
        [InlineData("POST", null, "gzip, chunked", false)]
        [InlineData("POST", null, "gzip", true)]
        [InlineData("GET", null, null, false)]
        [InlineData("DELETE", null, null, false)]
        public void IsUnframedBody_MethodAndFraming_MatchesTheListenersRule(string method, string? contentLength, string? te, bool expected)
        {
            Assert.Equal(expected, HeadlessControlServer.IsUnframedBody(method, contentLength, te));
        }

        [Fact]
        public void RecordStop_NoBodyNoContentLength_Rejected411BeforeHandlerAndStatusAgrees()
        {
            var ((code, _), dispatched) = Dispatched(() => Raw("POST", "/record/stop", body: null));

            // The handler never ran: a dispatched stop while idle would have been a 409 "not recording"
            // inside the server, and the dispatch counter would have moved.
            Assert.Equal(411, code);
            Assert.Equal(0, dispatched);
            Assert.Equal("idle", State());
        }

        [Fact]
        public void RecordStart_NoBodyNoContentLength_Rejected411AndNoRecordingStarted()
        {
            var ((code, _), dispatched) = Dispatched(() => Raw("POST", "/record/start", body: null));

            Assert.Equal(411, code);
            Assert.Equal(0, dispatched);
            Assert.Equal("idle", State());
        }

        [Theory]
        [InlineData("/record/shot")]
        [InlineData("/account/refresh")]
        [InlineData("/recordings/some-id/rename")]
        [InlineData("/recordings/some-id/subtitles")]
        [InlineData("/recordings/some-id/walkthrough")]
        public void OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(string path)
        {
            var ((code, _), dispatched) = Dispatched(() => Raw("POST", path, body: null));

            Assert.Equal(411, code);
            Assert.Equal(0, dispatched);
        }

        [Fact]
        public void RecordStop_WithJsonBody_IsDispatchedAndAnswersItself()
        {
            // The presence arm: the same endpoint WITH framing reaches the handler (counter moves) and
            // gets the handler's own answer - 409 "not recording", since the service is idle.
            var ((code, text), dispatched) = Dispatched(() => Raw("POST", "/record/stop", body: "{}"));

            Assert.Equal(409, code);
            Assert.Contains("not recording", text);
            Assert.Equal(1, dispatched);
        }

        [Fact]
        public void RecordStop_ContentLengthZero_IsDispatched()
        {
            // curl -d '' and most HTTP libraries send "Content-Length: 0": framed, so it is served.
            var ((code, text), dispatched) = Dispatched(() => Raw("POST", "/record/stop", body: ""));

            Assert.Equal(409, code);
            Assert.Contains("not recording", text);
            Assert.Equal(1, dispatched);
        }

        /// <summary>
        /// Runs one request and returns how many requests reached a handler because of it. On Linux the
        /// listener sends its own 411 BEFORE our loop handles the request, so the counter cannot be read
        /// straight after the response - that read raced the handler and let a disabled guard pass (the
        /// mutation run caught it). A GET /status afterwards is the barrier: the server handles requests
        /// one at a time in arrival order, so once /status is answered the earlier request is finished.
        /// The barrier's own dispatch is subtracted.
        /// </summary>
        private (T Result, int Dispatched) Dispatched<T>(Func<T> request)
        {
            int before = _server.RequestsDispatched;
            T result = request();
            State();
            return (result, _server.RequestsDispatched - before - 1);
        }

        private string State()
        {
            var (code, text) = Raw("GET", "/status", body: null);
            Assert.Equal(200, code);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("State").GetString() ?? "";
        }

        /// <summary>One HTTP/1.1 request on a fresh socket. body null = no Content-Length header at all.</summary>
        private (int Code, string Text) Raw(string method, string path, string? body)
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, _server.Port);
            client.ReceiveTimeout = 10000;
            using var stream = client.GetStream();

            var req = new StringBuilder();
            req.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            req.Append("Host: 127.0.0.1:").Append(_server.Port).Append("\r\n");
            req.Append("Connection: close\r\n");
            byte[] bodyBytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
            if (body != null)
            {
                req.Append("Content-Type: application/json\r\n");
                req.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            }
            req.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(req.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            string response = Encoding.UTF8.GetString(ms.ToArray());
            Assert.StartsWith("HTTP/1.1 ", response);   // an empty read is a broken instrument, not a pass
            int code = int.Parse(response.Substring(9, 3));
            int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return (code, split < 0 ? "" : response.Substring(split + 4));
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
