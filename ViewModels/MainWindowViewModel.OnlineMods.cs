using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<OnlineModInfo> OnlineMods { get; } = [];
    public ObservableCollection<OnlineModInfo> VisibleOnlineMods { get; } = [];
    private readonly ModRepositoryService _repositoryService;
    private CancellationTokenSource? _browseRequest;
    private bool _browseReady;
    private int _compactCapacity;
    private int _listCapacity;
    private int _displayedPageSize = 1;
    private int _installedRefreshGeneration;

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowsePageStatus), nameof(CanPreviousBrowsePage), nameof(CanNextBrowsePage))]
    [NotifyCanExecuteChangedFor(nameof(PreviousBrowsePageCommand), nameof(NextBrowsePageCommand))]
    public partial int BrowsePage { get; private set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowsePageStatus), nameof(CanNextBrowsePage))]
    [NotifyCanExecuteChangedFor(nameof(NextBrowsePageCommand))]
    public partial int BrowseTotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviousBrowsePage), nameof(CanNextBrowsePage), nameof(HasNoBrowseResults))]
    [NotifyCanExecuteChangedFor(nameof(PreviousBrowsePageCommand), nameof(NextBrowsePageCommand))]
    public partial bool IsBrowseLoading { get; private set; }

    [ObservableProperty]
    public partial double CompactCardHeight { get; private set; } = 300;

    public bool CanPreviousBrowsePage => !IsBrowseLoading && BrowsePage > 1;
    public bool CanNextBrowsePage => !IsBrowseLoading && BrowsePage < TotalBrowsePages;
    public bool HasNoBrowseResults => !IsBrowseLoading && VisibleOnlineMods.Count == 0;
    private int TotalBrowsePages => Math.Max(1, (int)((BrowseTotalCount + (long)_displayedPageSize - 1) / _displayedPageSize));
    public string BrowsePageStatus => LocalizationService.Instance.Format(
        "BrowsePageStatus", BrowsePage, TotalBrowsePages, BrowseTotalCount);
    public event EventHandler? BrowsePageChanged;

    [RelayCommand]
    private async Task RefreshMods()
    {
        _browseReady = true;
        _repositoryService.ClearHashMatches();
        await LoadBrowsePageAsync(BrowsePage);
        await RefreshInstalledCatalogAsync();
        ShowLibraryMods();
    }

    [RelayCommand(CanExecute = nameof(CanPreviousBrowsePage))]
    private Task PreviousBrowsePage() => LoadBrowsePageAsync(BrowsePage - 1, resetScroll: true);

    [RelayCommand(CanExecute = nameof(CanNextBrowsePage))]
    private Task NextBrowsePage() => LoadBrowsePageAsync(BrowsePage + 1, resetScroll: true);

    private async Task LoadBrowsePageAsync(int page, int delayMilliseconds = 0, bool resetScroll = false)
    {
        if (!_browseReady)
            return;
        var pageSize = IsCompactView ? _compactCapacity : _listCapacity;
        if (pageSize == 0)
            return;
        _browseRequest?.Cancel();
        var request = new CancellationTokenSource();
        _browseRequest = request;
        var keyword = SearchText;
        var sort = SortIndex;
        IsBrowseLoading = true;
        try
        {
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds, request.Token);
            var result = await _repositoryService.GetPageAsync(page, pageSize, keyword, sort, request.Token);
            var lastPage = Math.Max(1, (int)((result.TotalCount + (long)pageSize - 1) / pageSize));
            if (page > lastPage)
                result = await _repositoryService.GetPageAsync(lastPage, pageSize, keyword, sort, request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_browseRequest, request))
                return;

            var selectedId = BrowseSelection?.Id;
            var items = result.Items.Select(RegisterOnlineMod).ToList();
            VisibleOnlineMods.Clear();
            foreach (var item in items)
            {
                VisibleOnlineMods.Add(item);
                _ = _repositoryService.LoadThumbnailAsync(item);
            }
            _displayedPageSize = result.PageSize;
            BrowsePage = result.Page;
            BrowseTotalCount = result.TotalCount;
            OnPropertyChanged(nameof(BrowsePageStatus));
            BrowseSelection = items.FirstOrDefault(mod => mod.Id == selectedId);
            RefreshUpdateStatuses();
            if (SelectedMod != null)
                _ = _repositoryService.LoadChangelogAsync(SelectedMod);
            if (resetScroll || result.Page != page)
                BrowsePageChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_browseRequest, request))
                ReportError("StatusRefreshFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_browseRequest, request))
            {
                _browseRequest = null;
                IsBrowseLoading = false;
            }
            request.Dispose();
        }
    }

    internal void SetListCapacity(int capacity)
    {
        if (_listCapacity == capacity)
            return;
        _listCapacity = capacity;
        if (!IsCompactView)
            ReloadBrowseLayout(200);
    }

    internal void SetCompactLayout(CompactPageLayout layout)
    {
        CompactCardHeight = layout.CardHeight;
        if (_compactCapacity == layout.Capacity)
            return;
        _compactCapacity = layout.Capacity;
        if (IsCompactView)
            ReloadBrowseLayout(200);
    }

    private void ReloadBrowseLayout(int delayMilliseconds = 0)
    {
        var pageSize = IsCompactView ? _compactCapacity : _listCapacity;
        if (!_browseReady || pageSize == 0)
            return;
        var offset = (BrowsePage - 1L) * _displayedPageSize;
        var page = (int)(offset / pageSize + 1);
        _ = LoadBrowsePageAsync(page, delayMilliseconds, resetScroll: true);
    }

    partial void OnSortIndexChanged(int value)
    {
        _ = LoadBrowsePageAsync(1, resetScroll: true);
        PersistSettings();
    }

    partial void OnSearchTextChanged(string value) => _ = LoadBrowsePageAsync(1, 300, resetScroll: true);

    private OnlineModInfo RegisterOnlineMod(OnlineModInfo mod)
    {
        var existing = OnlineMods.FirstOrDefault(item => item.Id == mod.Id);
        if (existing == null)
        {
            OnlineMods.Add(mod);
            return mod;
        }

        var releaseChanged = existing.ChangelogRevision != mod.ChangelogRevision;
        existing.Name = mod.Name;
        existing.Author = mod.Author;
        existing.Ai = mod.Ai;
        existing.Development = mod.Development;
        existing.Tags = mod.Tags;
        existing.RebuildTags();
        existing.PublishedAt = mod.PublishedAt;
        existing.Downloads = mod.Downloads;
        existing.SetRelease(mod.Release);
        existing.Description = mod.Description;
        var thumbnailChanged = existing.ThumbnailUrl != mod.ThumbnailUrl;
        if (releaseChanged || thumbnailChanged)
        {
            existing.ThumbnailUrl = mod.ThumbnailUrl;
            existing.ThumbnailImage = null;
        }
        if (releaseChanged || thumbnailChanged || !existing.ImageUrls.SequenceEqual(mod.ImageUrls))
        {
            existing.ImageUrls = mod.ImageUrls;
            existing.GalleryImages.Clear();
            existing.GalleryLoaded = false;
        }
        if (releaseChanged)
        {
            existing.Changelog.Clear();
            existing.ChangelogTask = null;
        }
        return existing;
    }

    private async Task RefreshInstalledCatalogAsync()
    {
        var generation = ++_installedRefreshGeneration;
        var profileId = ActiveProfile?.Id;
        var ids = InstalledMods.Select(mod => mod.Id).ToArray();
        try
        {
            var results = new List<(string Id, OnlineModInfo? Mod)>();
            foreach (var id in ids)
                results.Add((id, await _repositoryService.GetSubmissionAsync(Guid.Parse(id))));
            if (generation != _installedRefreshGeneration || profileId != ActiveProfile?.Id)
                return;
            foreach (var (id, online) in results)
            {
                if (online != null)
                    RegisterOnlineMod(online);
                else
                {
                    var previous = OnlineMods.FirstOrDefault(mod => mod.Id == id);
                    if (previous != null)
                        OnlineMods.Remove(previous);
                }
            }
            RefreshUpdateStatuses();
        }
        catch (Exception ex)
        {
            if (generation == _installedRefreshGeneration && profileId == ActiveProfile?.Id)
                ReportError("StatusRefreshFailed", ex.Message);
        }
    }
}
