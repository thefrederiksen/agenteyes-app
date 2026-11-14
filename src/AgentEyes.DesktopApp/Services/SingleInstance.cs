using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentEyes.DesktopApp.Services
{
    /// <summary>
    /// One AgentEyes window per user (issue #107; Windows since #116). The first process listens; a
    /// second launch (from the app grid, the Start menu, say) asks it to show its window and exits, so
    /// the launcher and the login-started tray copy are always the same app.
    ///
    /// Two transports, one behaviour:
    ///   Linux, macOS  a Unix socket in $XDG_RUNTIME_DIR (the temp directory when that is unset, which
    ///                 is the macOS case). Unchanged from #107.
    ///   Windows       a named pipe, restricted to the current user (PipeOptions.CurrentUserOnly). A
    ///                 pipe disappears with its process, so a crashed first instance leaves nothing stale.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        /// <summary>How long a second launch waits for the first one's pipe before deciding it is first.</summary>
        private const int PipeConnectTimeoutMs = 300;

        private readonly Socket? _listener;
        private readonly string _path;
        private readonly NamedPipeServerStream? _firstPipe;
        private readonly string? _pipeName;
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _running = true;

        /// <summary>A command from a second launch: "show", "view:library", "details:ID", "preview:ID", "settings:account".</summary>
        public event Action<string>? CommandReceived;

        /// <summary>Which transport this instance listens on: "socket" or "pipe".</summary>
        public string Transport => _pipeName != null ? "pipe" : "socket";

        private SingleInstance(Socket listener, string path)
        {
            _listener = listener;
            _path = path;
            new Thread(Accept) { IsBackground = true, Name = "AgentEyes-single-instance" }.Start();
        }

        private SingleInstance(NamedPipeServerStream firstPipe, string pipeName)
        {
            _firstPipe = firstPipe;
            _pipeName = pipeName;
            _path = pipeName;
            _ = Task.Run(ServePipesAsync);
        }

        public static string SocketPath => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } d ? d : Path.GetTempPath(),
            "agenteyes-app.sock");

        /// <summary>The Windows pipe name: one per user (the pipe namespace is machine-wide).</summary>
        public static string PipeName => "agenteyes-app-" + Environment.UserName;

        /// <summary>The owner of this user's instance, or null when another instance answered (and was
        /// sent <paramref name="command"/>). The transport is the platform's: a pipe on Windows, a
        /// Unix socket elsewhere.</summary>
        public static SingleInstance? TryAcquire(string command = "show") =>
            OperatingSystem.IsWindows() ? TryAcquirePipe(command, PipeName) : TryAcquireSocket(command, SocketPath);

        /// <summary>The Unix socket transport (Linux, macOS). Internal so the tests can name the path.</summary>
        internal static SingleInstance? TryAcquireSocket(string command, string path)
        {
            Log.Info($"[SingleInstance] TryAcquireSocket: {path} command='{command}'");
            if (File.Exists(path))
            {
                try
                {
                    using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    client.Connect(new UnixDomainSocketEndPoint(path));
                    client.Send(Encoding.UTF8.GetBytes(command + "\n"));
                    Log.Info($"[SingleInstance] TryAcquire: another instance is running - sent it '{command}'");
                    return null;
                }
                catch (SocketException ex)
                {
                    Log.Info($"[SingleInstance] TryAcquire: stale socket {path} ({ex.SocketErrorCode}) - taking it over");
                    File.Delete(path);
                }
            }
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            Log.Info($"[SingleInstance] TryAcquire: listening on {path}");
            return new SingleInstance(listener, path);
        }

        /// <summary>
        /// The named-pipe transport (Windows). A second launch connects to the first one's pipe and
        /// writes its command; when nothing answers within <see cref="PipeConnectTimeoutMs"/>, this
        /// process is the first and creates the pipe itself, before returning, so a launch that follows
        /// finds it. Internal so the tests can name the pipe (.NET carries named pipes on Linux too,
        /// which is how the suite exercises this path on the Linux CI job).
        /// </summary>
        internal static SingleInstance? TryAcquirePipe(string command, string pipeName)
        {
            Log.Info($"[SingleInstance] TryAcquirePipe: {pipeName} command='{command}'");
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly))
            {
                try
                {
                    client.Connect(PipeConnectTimeoutMs);
                }
                catch (TimeoutException)
                {
                    client.Dispose();
                    var first = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    Log.Info($"[SingleInstance] TryAcquire: listening on pipe {pipeName}");
                    return new SingleInstance(first, pipeName);
                }
                byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
            }
            Log.Info($"[SingleInstance] TryAcquire: another instance is running - sent it '{command}'");
            return null;
        }

        private void Accept()
        {
            while (_running)
            {
                try
                {
                    using var s = _listener!.Accept();
                    var buf = new byte[512];
                    int n = s.Receive(buf);
                    string cmd = Encoding.UTF8.GetString(buf, 0, n).Trim();
                    Log.Info($"[SingleInstance] Accept: '{cmd}'");
                    if (cmd.Length > 0) CommandReceived?.Invoke(cmd);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    if (!_running) return;
                    Log.Error("[SingleInstance] Accept FAILED", ex);
                }
            }
        }

        /// <summary>One pipe instance per second launch: wait, read its one line, start the next.</summary>
        private async Task ServePipesAsync()
        {
            NamedPipeServerStream? server = _firstPipe;
            while (_running)
            {
                try
                {
                    server ??= new NamedPipeServerStream(_pipeName!, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string cmd = ((await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false)) ?? "").Trim();
                    Log.Info($"[SingleInstance] Accept: '{cmd}'");
                    if (cmd.Length > 0) CommandReceived?.Invoke(cmd);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    if (!_running) return;
                    Log.Error("[SingleInstance] Accept FAILED (pipe)", ex);
                }
                finally
                {
                    server?.Dispose();
                    server = null;
                }
            }
        }

        public void Dispose()
        {
            Log.Info($"[SingleInstance] Dispose: {Transport} {_path}");
            _running = false;
            _stop.Cancel();
            if (_listener == null) return;
            _listener.Dispose();
            try { File.Delete(_path); }
            catch (IOException ex) { Log.Warn($"[SingleInstance] Dispose: could not remove {_path}: {ex.Message}"); }
        }
    }
}
