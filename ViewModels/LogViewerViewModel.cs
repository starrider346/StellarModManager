using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;

namespace StellarModManager.ViewModels;

public partial class LogViewerViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);
    private readonly List<LogEntry> _entries = [];
    private CancellationTokenSource? _refresh;
    private string? _loaderPath;
    private bool _active;
    private bool _updatingFiles;
    private string? _readStatus;

    internal enum ScrollPosition {
        End, Follow }

    internal event Action<ScrollPosition>? EntriesChanging;

    public AvaloniaList<LogFile> Files { get; } = [];

    [ObservableProperty]
    public partial AvaloniaList<LogEntry> VisibleEntries { get; private set; } = [];

    [ObservableProperty]
    public partial LogFile? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSelected), nameof(IsErrorSelected), nameof(IsWarningSelected), nameof(IsInfoSelected))]
    public partial LogLevel SelectedLevel { get; private set; }

    public bool IsAllSelected => SelectedLevel == LogLevel.All;
    public bool IsErrorSelected => SelectedLevel == LogLevel.Error;
    public bool IsWarningSelected => SelectedLevel == LogLevel.Warning;
    public bool IsInfoSelected => SelectedLevel == LogLevel.Info;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial bool CanCopy { get; private set; }

    internal void SetGamePath(string gamePath)
    {
        var loaderPath = Path.Combine(gamePath, "MelonLoader");
        if (_loaderPath == loaderPath)
            return;

        _loaderPath = loaderPath;
        if (_active)
            OpenLatest();
    }

    internal void Start()
    {
        if (_active)
            return;

        _active = true;
        OpenLatest();
    }

    internal void Stop()
    {
        _active = false;
        _refresh?.Cancel();
        _refresh = null;
        _entries.Clear();
        VisibleEntries = [];
        CanCopy = false;
    }

    private void OpenLatest()
    {
        _updatingFiles = true;
        Files.Clear();
        if (_loaderPath != null)
            Files.Add(new(Path.Combine(_loaderPath, "Latest.log"), IsLatest: true));

        SelectedFile = Files.FirstOrDefault();
        SearchText = null;
        SelectedLevel = LogLevel.All;
        _updatingFiles = false;
        RestartRefresh();
    }

    partial void OnSelectedFileChanged(LogFile? value)
    {
        if (!_updatingFiles && value != null && _active)
            RestartRefresh();
    }

    partial void OnSearchTextChanged(string? value)
    {
        if (!_updatingFiles)
            RebuildVisibleEntries();
    }

    partial void OnSelectedLevelChanged(LogLevel value)
    {
        if (!_updatingFiles)
            RebuildVisibleEntries();
    }

    [RelayCommand]
    private void SelectLevel(LogLevel level) => SelectedLevel = level;

    internal async Task<string?> CopyTextAsync(LogFile file)
    {
        var refresh = _refresh;
        if (refresh == null || file != SelectedFile || !CanCopy)
            return null;

        try
        {
            var text = await MelonLogReader.ReadTextAsync(file.Path, refresh.Token);
            return _refresh == refresh ? text : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void RestartRefresh()
    {
        _refresh?.Cancel();
        _refresh = null;
        _entries.Clear();
        _readStatus = "LogLoading";
        CanCopy = false;
        RebuildVisibleEntries();
        if (_loaderPath == null || SelectedFile == null)
            return;

        var refresh = _refresh = new CancellationTokenSource();
        _ = RefreshAsync(_loaderPath, SelectedFile, refresh);
    }

    private async Task RefreshAsync(string loaderPath, LogFile file, CancellationTokenSource refresh)
    {
        var cancellation = refresh.Token;
        MelonLogReader.Cursor? position = null;
        string? mirrorPath = null;
        var archiveRead = false;
        try
        {
            while (true)
            {
                var update = await Task.Run(() => ReadUpdate(loaderPath, file, archiveRead, position, mirrorPath, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();

                if (update.Archives is { } archives)
                {
                    if (archives.MirrorPath != null && archives.MirrorPath != mirrorPath)
                        position = null;
                    mirrorPath = archives.MirrorPath;
                    UpdateFiles(archives.History);
                    cancellation.ThrowIfCancellationRequested();
                }

                if (update.Batch is { } batch)
                {
                    position = batch.Position;
                    _readStatus = null;
                    CanCopy = true;
                    ApplyBatch(batch);
                }
                else if (update.StatusKey is { } status)
                {
                    _readStatus = status;
                    CanCopy = false;
                    if (status == "LogWaiting")
                    {
                        position = null;
                        _entries.Clear();
                        if (VisibleEntries.Count > 0)
                            RebuildVisibleEntries();
                    }
                }

                archiveRead = !file.IsLatest;
                UpdateStatus();
                await Task.Delay(RefreshInterval, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_refresh == refresh)
                _refresh = null;
            refresh.Dispose();
        }
    }

    private static (MelonLogReader.ArchiveList? Archives, MelonLogReader.Batch? Batch, string? StatusKey) ReadUpdate(
        string loaderPath, LogFile file, bool archiveRead, MelonLogReader.Cursor? position, string? mirrorPath, CancellationToken cancellation)
    {
        MelonLogReader.ArchiveList? archives = null;
        try
        {
            archives = MelonLogReader.FindArchives(loaderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {}

        if (archiveRead)
            return (archives, null, null);

        cancellation.ThrowIfCancellationRequested();
        if (archives?.MirrorPath != null && archives.MirrorPath != mirrorPath)
            position = null;

        try
        {
            return (archives, MelonLogReader.Read(file, position, cancellation), null);
        }
        catch (Exception ex) when (file.IsLatest && (ex is FileNotFoundException or DirectoryNotFoundException))
        {
            return (archives, null, "LogWaiting");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (archives, null, "LogReadFailed");
        }
    }

    private void UpdateFiles(LogFile[] history)
    {
        if (Files.Skip(1).SequenceEqual(history))
            return;

        var selected = SelectedFile;
        _updatingFiles = true;
        for (var i = 0; i < history.Length; i++)
        {
            var index = Files.IndexOf(history[i]);
            if (index < 0)
                Files.Insert(i + 1, history[i]);
            else if (index != i + 1)
                Files.Move(index, i + 1);
        }
        while (Files.Count > history.Length + 1)
            Files.RemoveAt(Files.Count - 1);

        SelectedFile = selected != null && Files.Contains(selected) ? selected : Files[0];
        _updatingFiles = false;
        if (SelectedFile != selected)
            RestartRefresh();
    }

    private void ApplyBatch(MelonLogReader.Batch batch)
    {
        if (batch.Reset)
        {
            _entries.Clear();
            _entries.AddRange(batch.Entries);
            RebuildVisibleEntries();
            return;
        }

        if (batch.Continuation is { } continuation)
        {
            if (_entries.Count == 0)
            {
                var entry = new LogEntry(LogLevel.Info, continuation[1..]);
                _entries.Add(entry);
                if (MatchesFilters(entry))
                {
                    EntriesChanging?.Invoke(ScrollPosition.Follow);
                    VisibleEntries.Add(entry);
                }
            }
            else
            {
                var previous = _entries[^1];
                var updated = previous with { Text = previous.Text + continuation };
                _entries[^1] = updated;
                if (MatchesFilters(updated))
                {
                    EntriesChanging?.Invoke(ScrollPosition.Follow);
                    if (MatchesFilters(previous))
                        VisibleEntries[^1] = updated;
                    else
                        VisibleEntries.Add(updated);
                }
            }
        }

        _entries.AddRange(batch.Entries);
        var visible = batch.Entries.Where(MatchesFilters).ToArray();
        if (visible.Length > 0)
        {
            EntriesChanging?.Invoke(ScrollPosition.Follow);
            VisibleEntries.AddRange(visible);
        }
    }

    private bool MatchesFilters(LogEntry entry) =>
        (SelectedLevel == LogLevel.All || entry.Level == SelectedLevel) &&
        (string.IsNullOrWhiteSpace(SearchText) || entry.Text.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void RebuildVisibleEntries()
    {
        EntriesChanging?.Invoke(ScrollPosition.End);
        VisibleEntries = new(_entries.Where(MatchesFilters));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var key = _readStatus ?? (_entries.Count == 0 ? "LogEmpty" : VisibleEntries.Count == 0 ? "LogNoMatches" : null);
        StatusText = key == null ? null : LocalizationService.Instance[key];
    }
}
