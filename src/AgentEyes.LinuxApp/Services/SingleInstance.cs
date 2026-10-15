using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AgentEyes.LinuxApp.Services
{
    /// <summary>
    /// One AgentEyes window per user (issue #107). The first process listens on a Unix socket in
    /// $XDG_RUNTIME_DIR; a second launch (from the app grid, say) asks it to show its window and exits,
    /// so the dock launcher and the login-started tray copy are always the same app.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private readonly Socket _listener;
        private readonly string _path;
        private volatile bool _running = true;

        /// <summary>A command from a second launch: "show", "view:library", "details:ID", "preview:ID", "settings:account".</summary>
        public event Action<string>? CommandReceived;

        private SingleInstance(Socket listener, string path)
        {
            _listener = listener;
            _path = path;
            new Thread(Accept) { IsBackground = true, Name = "AgentEyes-single-instance" }.Start();
        }

        public static string SocketPath => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } d ? d : Path.GetTempPath(),
            "agenteyes-app.sock");

        /// <summary>The owner of the socket, or null when another instance answered (and was asked to show).</summary>
        public static SingleInstance? TryAcquire(string command = "show")
        {
            string path = SocketPath;
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

        private void Accept()
        {
            while (_running)
            {
                try
                {
                    using var s = _listener.Accept();
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

        public void Dispose()
        {
            _running = false;
            _listener.Dispose();
            try { File.Delete(_path); }
            catch (IOException ex) { Log.Warn($"[SingleInstance] Dispose: could not remove {_path}: {ex.Message}"); }
        }
    }
}
