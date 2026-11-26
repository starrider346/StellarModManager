namespace StellarModManager.Models;

public enum LogLevel { All, Error, Warning, Info }

public sealed record LogEntry(LogLevel Level, string Text)
{
    public bool IsError => Level == LogLevel.Error;
    public bool IsWarning => Level == LogLevel.Warning;
}

public sealed record LogFile(string Path, bool IsLatest = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
