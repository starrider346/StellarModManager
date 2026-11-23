using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using StellarModManager.Models;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StellarModManager.ViewModels;

namespace StellarModManager.Views;

public partial class MainWindow : Window
{
    private readonly Dictionary<Control, Control?> _overlayFocusOrigins = [];
    private Control[] _overlays = [];
    private Control? _activeOverlay;
    private ListBoxItem? _pressedModItem;
    private bool _overlayUpdatePending;

    public MainWindow()
    {
        InitializeComponent();

        _overlays = [UpdateOverlay, ReplacementOverlay, DependenciesOverlay, RemoveOverlay, DetailsOverlay];
        foreach (var overlay in _overlays)
        {
            overlay.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsVisibleProperty)
                    QueueOverlayUpdate();
            };
        }

        AddHandler(PointerPressedEvent, OnModPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnModPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private static ListBoxItem? GetModListItem(object? source)
    {
        if (source is not Visual visual)
            return null;

        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ancestor is Button)
                return null;
            if (ancestor is ListBoxItem item)
                return item.FindAncestorOfType<ListBox>()?.Classes.Contains("modItems") == true ? item : null;
        }

        return null;
    }

    private void OnModPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedModItem = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? GetModListItem(e.Source) : null;
    }

    private void OnModPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var item = _pressedModItem;
        _pressedModItem = null;
        if (e.InitialPressMouseButton == MouseButton.Left && item != null && item == GetModListItem(e.Source) &&
            item.GetVisualsAt(e.GetPosition(item)).Any() && OpenModItem(item))
            e.Handled = true;
    }

    private bool OpenModItem(ListBoxItem item)
    {
        if (!item.IsEffectivelyEnabled || item.DataContext is not ModInfo mod || DataContext is not MainWindowViewModel vm)
            return false;

        item.Focus();
        vm.OpenModDetails(mod);
        return vm.IsModDetailsOpen;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm)
        {
            if (ProfilePopup.IsOpen)
                DismissProfileMenu(vm, e.Source);
            else if (UpdateOverlay.IsVisible)
            {
                if (vm.IsDownloadingUpdate)
                    return;
                vm.CloseUpdatePanelCommand.Execute(null);
            }
            else if (ReplacementOverlay.IsVisible)
                vm.CancelReplacementCommand.Execute(null);
            else if (DependenciesOverlay.IsVisible)
                vm.CancelDependenciesCommand.Execute(null);
            else if (RemoveOverlay.IsVisible)
                vm.CancelRemoveCommand.Execute(null);
            else if (DetailsOverlay.IsVisible)
                vm.CloseModDetailsCommand.Execute(null);
            else
                return;

            e.Handled = true;
        }
        else if (e.Key == Key.Enter && GetModListItem(e.Source) is { } item && OpenModItem(item))
        {
            e.Handled = true;
        }
    }

    private void QueueOverlayUpdate()
    {
        if (_overlayUpdatePending)
            return;

        _overlayUpdatePending = true;
        Dispatcher.UIThread.Post(UpdateOverlayFocus, DispatcherPriority.Loaded);
    }

    private void UpdateOverlayFocus()
    {
        _overlayUpdatePending = false;
        var next = _overlays.FirstOrDefault(overlay => overlay.IsVisible);
        if (next == _activeOverlay)
            return;

        var focused = FocusManager?.GetFocusedElement() as Control;
        Control? restore = null;
        if (_activeOverlay is { IsVisible: false } previous)
            _overlayFocusOrigins.Remove(previous, out restore);

        _activeOverlay = next;
        HeaderActions.IsEnabled = next == null;
        MainContent.IsEnabled = next == null;
        foreach (var overlay in _overlays)
            overlay.IsEnabled = overlay == next;

        if (next != null)
        {
            _overlayFocusOrigins.TryAdd(next, restore ?? focused);
            if (restore == null || !next.IsVisualAncestorOf(restore) || !restore.Focus())
                FocusFirstControl(next);
            return;
        }

        if (restore != null && !restore.IsEffectivelyVisible && DetailsOverlay.IsVisualAncestorOf(restore))
            _overlayFocusOrigins.Remove(DetailsOverlay, out restore);
        if (restore?.Focus() != true)
            NavigationToggle.Focus();
    }

    private static void FocusFirstControl(Control root)
    {
        var first = root.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Focusable);
        if (first?.Focus() != true)
            root.Focus();
    }

    private void OnProfilePopupOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => FocusFirstControl(ProfileMenu), DispatcherPriority.Loaded);

    private void OnProfilePopupClosed(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlays.All(overlay => !overlay.IsVisible))
                ProfileButton.Focus();
        }, DispatcherPriority.Loaded);

    private void OnProfileMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm)
        {
            DismissProfileMenu(vm, e.Source);
            e.Handled = true;
        }
    }

    private static void DismissProfileMenu(MainWindowViewModel vm, object? source)
    {
        if (source is TextBox { DataContext: ModProfile profile })
            vm.CancelRenameCommand.Execute(profile);
        else
            vm.IsProfilesDropdownOpen = false;
    }

    // Lets the styles swap the maximize icon for the restore icon
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
            Classes.Set("maximized", WindowState == WindowState.Maximized);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnNewProfileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
        {
            vm.CreateProfileCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ModProfile profile && DataContext is MainWindowViewModel vm)
        {
            if (e.Key == Key.Enter)
            {
                vm.CommitRenameCommand.Execute(profile);
                e.Handled = true;
            }
        }
    }
}
