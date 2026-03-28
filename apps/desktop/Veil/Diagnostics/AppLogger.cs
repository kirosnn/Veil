using System.Text;

namespace Veil.Diagnostics;

internal static class AppLogger
{
    private static readonly Lock _lock = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "veil.log");

    internal static void Info(string message)
    {
        Write("INFO", message);
    }

    internal static void Error(string message, Exception? exception = null)
    {
        var builder = new StringBuilder(message);
        if (exception != null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        Write("ERROR", builder.ToString());
    }

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
    }
}
