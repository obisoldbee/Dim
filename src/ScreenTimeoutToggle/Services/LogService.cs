namespace OBDim.Services;

/// <summary>
/// Lightweight file-based logger. Writes to %AppData%\ScreenTimeoutToggle\log.txt.
/// No third-party dependencies. Best-effort: silently swallows I/O errors.
/// </summary>
public static class LogService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenTimeoutToggle",
        "log.txt");

    /// <summary>
    /// Redirects log output away from the real user log file.
    /// </summary>
    /// <remarks>
    /// v1.0.6: unit tests drive the same code paths as production (powercfg retries,
    /// config load failures, …) and every one of them used to append to the user's real
    /// log. A single test run added hundreds of synthetic lines, which (a) buries real
    /// evidence and (b) makes the 1&nbsp;MB trim discard genuine history first.
    /// Production never sets this, so behaviour is byte-for-byte identical.
    /// </remarks>
    internal static string? OverridePath { get; set; }

    /// <summary>Path actually used for writes; the override wins when set.</summary>
    private static string LogPath => OverridePath ?? DefaultPath;

    private const long MaxLogSize = 1 * 1024 * 1024; // 1 MB
    private const long TrimKeepSize = 100 * 1024;     // keep last 100 KB when trimming

    /// <summary>
    /// Writes a single log line with timestamp and level.
    /// </summary>
    public static void Log(string level, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            TrimIfNeeded();

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch
        {
            // Best effort — logging must never crash the app
        }
    }

    /// <summary>Logs an informational message.</summary>
    public static void Info(string msg) => Log("INFO", msg);

    /// <summary>Logs a warning message.</summary>
    public static void Warn(string msg) => Log("WARN", msg);

    /// <summary>Logs an error message with optional exception details.</summary>
    public static void Error(string msg, Exception? ex = null) =>
        Log("ERROR", ex == null ? msg : $"{msg}\n{ex}");

    /// <summary>
    /// Trims the log file if it exceeds MaxLogSize, keeping the most recent TrimKeepSize bytes.
    /// </summary>
    private static void TrimIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var info = new FileInfo(LogPath);
            if (info.Length <= MaxLogSize) return;

            var bytes = File.ReadAllBytes(LogPath);
            if (bytes.Length <= TrimKeepSize) return;

            var trimmed = new byte[TrimKeepSize];
            var offset = bytes.Length - TrimKeepSize;
            Array.Copy(bytes, offset, trimmed, 0, TrimKeepSize);
            File.WriteAllBytes(LogPath, trimmed);
        }
        catch
        {
            // Best effort
        }
    }
}
