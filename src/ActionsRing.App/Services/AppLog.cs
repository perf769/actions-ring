using System.Globalization;
using System.Text;

namespace ActionsRing.App.Services;

internal static class AppLog
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ActionsRing",
        "logs");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "actions-ring.log");

    public static string CurrentPath => LogPath;

    public static void Info(string message) => Write("INF", message, null);
    public static void Warning(string message) => Write("WRN", message, null);
    public static void Error(string message, Exception exception) => Write("ERR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                    .Append(" [").Append(level).Append("] ")
                    .AppendLine(message);
                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is diagnostic only and must never take the ring down.
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaximumLogBytes)
        {
            return;
        }
        var previous = LogPath + ".1";
        File.Move(LogPath, previous, overwrite: true);
    }
}
