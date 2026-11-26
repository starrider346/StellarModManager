using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using StellarModManager.Models;

namespace StellarModManager.Services;

internal static partial class MelonLogReader
{
    private const int CheckpointSize = 128;

    internal sealed record Cursor(long Offset, DateTime LastWriteTime, byte[] FirstBytes, byte[] LastBytes, byte[] PartialLine);
    internal sealed record Batch(Cursor? Position, bool Reset, string? Continuation, IReadOnlyList<LogEntry> Entries);
    internal sealed record ArchiveList(string? MirrorPath, LogFile[] History);

    public static ArchiveList FindArchives(string loaderPath)
    {
        var folder = new DirectoryInfo(Path.Combine(loaderPath, "Logs"));
        try
        {
            var newest = folder.EnumerateFiles()
                .Where(file => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Take(6)
                .Select(file => new LogFile(file.FullName))
                .ToArray();

            return new(newest.FirstOrDefault()?.Path, newest.Skip(1).ToArray());
        }
        catch (DirectoryNotFoundException)
        {
            return new(null, []);
        }
    }

    public static Batch Read(LogFile file, Cursor? position, CancellationToken cancellation)
    {
        using var stream = Open(file.Path);
        var length = stream.Length;
        var lastWriteTime = File.GetLastWriteTimeUtc(file.Path);
        var reset = position is null || length < position.Offset ||
            (length == position.Offset && lastWriteTime != position.LastWriteTime) ||
            !Matches(stream, 0, position.FirstBytes) ||
            !Matches(stream, position.Offset - position.LastBytes.Length, position.LastBytes);

        var offset = reset ? 0 : position!.Offset;
        if (!reset && offset == length)
            return new(position, false, null, []);

        var firstBytes = file.IsLatest ? ReadCheckpoint(stream, 0, length) : [];
        var lastBytes = file.IsLatest ? ReadCheckpoint(stream, Math.Max(0, length - CheckpointSize), length) : [];
        stream.Position = offset;

        var buffer = new byte[16 * 1024];
        var line = new ArrayBufferWriter<byte>();
        if (!reset && position!.PartialLine.Length > 0)
            line.Write(position.PartialLine);

        var entries = new List<LogEntry>();
        var text = new StringBuilder();
        string? continuation = null;
        LogLevel? level = null;
        var firstLine = offset == line.WrittenCount;

        void AddLine(ReadOnlySpan<byte> bytes)
        {
            if (firstLine && bytes.StartsWith(Encoding.UTF8.Preamble))
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            firstLine = false;
            if (!bytes.IsEmpty && bytes[^1] == '\r')
                bytes = bytes[..^1];

            var value = Encoding.UTF8.GetString(bytes);
            var header = EntryHeader().Match(value);
            if (header.Success)
            {
                if (level is { } previousLevel)
                    entries.Add(new(previousLevel, text.ToString()));
                else if (text.Length > 0)
                    continuation = text.ToString();

                level = header.Groups[1].Value.ToUpperInvariant() switch
                {
                    "ERROR" => LogLevel.Error,
                    "WARNING" or "WARN" => LogLevel.Warning,
                    _ => LogLevel.Info
                };
                text.Clear().Append(value);
            }
            else
            {
                text.Append('\n').Append(value);
            }
        }

        while (stream.Position < length)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Min(length - stream.Position, buffer.Length);
            stream.ReadExactly(buffer.AsSpan(0, count));
            var bytes = buffer.AsSpan(0, count);
            for (var newline = bytes.IndexOf((byte)'\n'); newline >= 0; newline = bytes.IndexOf((byte)'\n'))
            {
                line.Write(bytes[..newline]);
                AddLine(line.WrittenSpan);
                line.Clear();
                bytes = bytes[(newline + 1)..];
            }
            line.Write(bytes);
        }

        if (!file.IsLatest && line.WrittenCount > 0)
            AddLine(line.WrittenSpan);

        if (level is { } finalLevel)
            entries.Add(new(finalLevel, text.ToString()));
        else if (text.Length > 0)
            continuation = text.ToString();

        if (reset && continuation is not null)
        {
            entries.Insert(0, new(LogLevel.Info, continuation[1..]));
            continuation = null;
        }

        if (file.IsLatest && (!Matches(stream, 0, firstBytes) || !Matches(stream, length - lastBytes.Length, lastBytes)))
            throw new IOException("The log changed during the read.");

        var nextPosition = file.IsLatest ? new Cursor(length, lastWriteTime, firstBytes, lastBytes, line.WrittenSpan.ToArray()) : null;
        return new(nextPosition, reset, continuation, entries);
    }

    public static async Task<string> ReadTextAsync(string path, CancellationToken cancellation)
    {
        using var reader = new StreamReader(Open(path));
        return await reader.ReadToEndAsync(cancellation).ConfigureAwait(false);
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);

    private static bool Matches(FileStream stream, long offset, byte[] expected)
    {
        Span<byte> bytes = stackalloc byte[expected.Length];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes.SequenceEqual(expected);
    }

    private static byte[] ReadCheckpoint(FileStream stream, long offset, long length)
    {
        var bytes = new byte[Math.Min(CheckpointSize, length - offset)];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?\][ \t]*(?:\[(ERROR|WARNING|WARN|INFO)\])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntryHeader();
}
