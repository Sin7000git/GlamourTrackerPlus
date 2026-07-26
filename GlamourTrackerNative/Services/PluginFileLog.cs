using System.Globalization;
using System.Text;

namespace GlamourTracker.Services;

/// <summary>Append-only file logging under ~/.config/glamour-tracker-plus/logs/app.log.</summary>
internal static class PluginFileLog
{
    private static readonly object Gate = new();
    private static string? logPath;

    public static string LogPath
    {
        get
        {
            if (logPath != null)
                return logPath;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".config", "glamour-tracker-plus", "logs");
            Directory.CreateDirectory(dir);
            logPath = Path.Combine(dir, "app.log");
            return logPath;
        }
    }

    public static void Write(string level, string area, string message)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} [{level}] {area}: {message}{Environment.NewLine}");
            lock (Gate)
                File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
        catch
        {
            // Never let logging break the plugin.
        }
    }

    public static void Error(string area, string message, Exception? ex = null)
    {
        if (ex == null)
        {
            Write("ERROR", area, message);
            return;
        }

        Write("ERROR", area, $"{message} — {ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            Write("ERROR", area, ex.StackTrace.ReplaceLineEndings(" | "));
    }

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Info(string area, string message) => Write("INFO", area, message);
}
