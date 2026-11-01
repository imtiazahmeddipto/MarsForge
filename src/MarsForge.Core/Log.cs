namespace MarsForge.Core;

/// <summary>A small text log (MarsForge.log next to the app), so problems can be found later.</summary>
public static class Log
{
    static readonly object Lock = new();
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        if (FilePath is null) return;
        try
        {
            lock (Lock) File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Error(string where, Exception ex) => Write($"{where}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");
}
