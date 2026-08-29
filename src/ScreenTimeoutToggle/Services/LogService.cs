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
    /// Serializes writers. <see cref="File.AppendAllText"/> opens the log with
    /// <c>FileShare.Read</c>, so two concurrent writes throw <see cref="IOException"/> and
    /// the losing line is dropped — usually the line describing the failure you are trying
    /// to diagnose. The trim and the append are one unit: without the lock, a trim
    /// running between another thread's trim and append would discard what was just appended.
    /// </summary>
    private static readonly object Sync = new();

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

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";

            lock (Sync)
            {
                TrimIfNeeded();
                File.AppendAllText(LogPath, line);
            }
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
    /// <remarks>
    /// Callers must hold <see cref="Sync"/>.
    /// <para>
    /// v1.0.7: this used to <see cref="File.ReadAllBytes"/> the entire file. Once the log
    /// passes MaxLogSize that is at least 1&nbsp;MB read on <em>every single</em>
    /// subsequent line — the steady state for a long-lived tray app. Only the tail is
    /// needed, so it is seeked to and read directly.
    /// </para>
    /// </remarks>
    private static void TrimIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var info = new FileInfo(LogPath);
            if (info.Length <= MaxLogSize) return;
            if (info.Length <= TrimKeepSize) return;

            byte[] trimmed;
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var keep = (int)Math.Min(TrimKeepSize, stream.Length);
                stream.Seek(-keep, SeekOrigin.End);

                trimmed = new byte[keep];
                var read = 0;
                while (read < keep)
                {
                    var n = stream.Read(trimmed, read, keep - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (read < keep)
                {
                    // The file shrank underneath us (another process trimmed it). Keep
                    // what was actually read rather than padding the tail with zeroes.
                    Array.Resize(ref trimmed, read);
                }
            }

            File.WriteAllBytes(LogPath, trimmed);
        }
        catch
        {
            // Best effort
        }
    }
}
