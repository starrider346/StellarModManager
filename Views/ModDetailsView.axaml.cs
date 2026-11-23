using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StellarModManager.Models;
using StellarModManager.Services;
using StellarModManager.ViewModels;

namespace StellarModManager.Views;

public partial class ModDetailsView : UserControl
{
    private MainWindowViewModel? _viewModel;
    private ModInfo? _displayedMod;
    private readonly DispatcherTimer _galleryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _galleryAttached;
    private bool _galleryPaused;
    private bool _galleryKeyboardFocused;
    private bool _galleryRevealPending;

    public ModDetailsView()
    {
        InitializeComponent();

        _galleryTimer.Tick += (_, _) =>
        {
            if (CanPlayGallery)
                GalleryThumbnails.SelectedIndex = (GalleryThumbnails.SelectedIndex + 1) % GalleryThumbnails.ItemCount;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _galleryAttached = true;
            UpdateGalleryPlayback();
            QueueGalleryReveal();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _galleryAttached = false;
            UpdateGalleryPlayback();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (!IsVisible)
                    _galleryPaused = false;
                UpdateGalleryPlayback();
            }
        };
        Gallery.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsPointerOverProperty)
                UpdateGalleryPlayback();
        };
        GalleryThumbnails.PropertyChanged += OnGalleryThumbnailsChanged;
        GalleryThumbnails.SelectionChanged += (_, _) =>
        {
            UpdateGalleryPlayback(restart: true);
            QueueGalleryReveal();
        };
        GalleryThumbnails.SizeChanged += (_, _) => QueueGalleryReveal();
        GalleryThumbnails.GotFocus += (_, e) => SetGalleryKeyboardFocus(e.NavigationMethod != NavigationMethod.Pointer);
        GalleryThumbnails.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Space or Key.Enter)
                SetGalleryPaused(true);
        }, RoutingStrategies.Tunnel);
        GalleryThumbnails.AddHandler(PointerPressedEvent, (_, _) => SetGalleryKeyboardFocus(false), RoutingStrategies.Tunnel);
        GalleryThumbnails.AddHandler(TappedEvent, OnGalleryThumbnailTapped, handledEventsToo: true);
        GalleryThumbnails.AddHandler(DoubleTappedEvent, OnGalleryThumbnailTapped, handledEventsToo: true);
        GalleryThumbnails.ContainerPrepared += (_, e) => AutomationProperties.SetName(e.Container,
            LocalizationService.Instance.Format("GalleryScreenshot", e.Index + 1));
    }

    // Start each newly selected mod at the top of the pane
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MainWindowViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.DetailsMod))
        {
            var mod = _viewModel?.DetailsMod;
            if (_displayedMod?.Id != mod?.Id)
            {
                DetailsScroll.ScrollToHome();
                ReleaseScroll.ScrollToHome();
            }

            _displayedMod = mod;
            Grid.SetColumnSpan(DetailsScroll, mod is InstalledModInfo { IsExternal: true } ? 2 : 1);
        }
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _viewModel.CloseModDetailsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDetailsViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateGalleryPreviewHeight(e.NewSize.Height);

    private void UpdateGalleryPreviewHeight(double height)
    {
        if (height > 0)
            GalleryPreview.Height = Math.Clamp(height - (GalleryThumbnails.IsVisible ? 250 : 202), 160, 300);
    }

    private void OnGalleryThumbnailsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty)
        {
            SetGalleryPaused(false);
            GalleryThumbnails.SelectedIndex = GalleryThumbnails.ItemCount > 0 ? 0 : -1;
            if (GalleryThumbnails.Scroll is ScrollViewer scroll)
                scroll.Offset = default;
        }

        if (e.Property == ItemsControl.ItemsSourceProperty || e.Property == ItemsControl.ItemCountProperty)
        {
            GalleryThumbnails.IsVisible = GalleryThumbnails.ItemCount > 1;
            UpdateGalleryPreviewHeight(DetailsScroll.Bounds.Height);
            UpdateGalleryPlayback(restart: true);
            QueueGalleryReveal();
        }

        if (e.Property == IsKeyboardFocusWithinProperty)
            UpdateGalleryPlayback();
    }

    private void SetGalleryPaused(bool paused)
    {
        _galleryPaused = paused;
        UpdateGalleryPlayback();
    }

    private void OnGalleryThumbnailTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            SetGalleryPaused(true);
    }

    private void SetGalleryKeyboardFocus(bool focused)
    {
        _galleryKeyboardFocused = focused;
        UpdateGalleryPlayback();
    }

    private bool CanPlayGallery => _galleryAttached && IsEffectivelyVisible && GalleryThumbnails.ItemCount > 1 &&
        !_galleryPaused && !Gallery.IsPointerOver && !(_galleryKeyboardFocused && GalleryThumbnails.IsKeyboardFocusWithin);

    private void UpdateGalleryPlayback(bool restart = false)
    {
        var canPlay = CanPlayGallery;
        if (!canPlay || restart)
            _galleryTimer.Stop();
        if (canPlay && !_galleryTimer.IsEnabled)
            _galleryTimer.Start();
    }

    private void QueueGalleryReveal()
    {
        if (!_galleryAttached || _galleryRevealPending)
            return;

        _galleryRevealPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _galleryRevealPending = false;
            RevealSelectedThumbnail();
        }, DispatcherPriority.Loaded);
    }

    private void RevealSelectedThumbnail()
    {
        if (!_galleryAttached || !GalleryThumbnails.IsEffectivelyVisible ||
            GalleryThumbnails.Scroll is not ScrollViewer { Content: Control content } scroll ||
            scroll.Viewport.Width <= 0 || GalleryThumbnails.ContainerFromIndex(GalleryThumbnails.SelectedIndex) is not { } item ||
            item.TranslatePoint(default, content) is not { } position)
            return;

        var offset = scroll.Offset;
        var right = position.X + item.Bounds.Width;
        var x = position.X < offset.X ? position.X : Math.Max(offset.X, right - scroll.Viewport.Width);
        scroll.Offset = new Vector(Math.Clamp(x, 0, scroll.ScrollBarMaximum.X), offset.Y);
    }
}
