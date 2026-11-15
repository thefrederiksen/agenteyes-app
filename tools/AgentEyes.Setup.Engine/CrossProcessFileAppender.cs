using System.IO;
using System.Text;
using System.Threading;

namespace AgentEyes.Setup.Engine;

/// <summary>Appends one complete line while coordinating concurrent installer processes.</summary>
public static class CrossProcessFileAppender
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void AppendLine(string path, string text)
    {
        const int MaxLockAttempts = 1000;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Lock(0, 1);
                try
                {
                    stream.Seek(0, SeekOrigin.End);
                    byte[] bytes = Utf8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                    return;
                }
                finally
                {
                    stream.Unlock(0, 1);
                }
            }
            catch (IOException) when (attempt < MaxLockAttempts)
            {
                Thread.Sleep(1);
            }
        }
    }
}
