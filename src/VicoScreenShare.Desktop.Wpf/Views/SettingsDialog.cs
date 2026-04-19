namespace VicoScreenShare.Desktop.App.Views;

using System;
using System.Windows;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Desktop.App.Services;
using VicoScreenShare.Desktop.App.ViewModels;

/// <summary>
/// Opens Settings as an in-window modal overlay. All three entry points
/// (home, room, capture test) route through here. The underlying page stays
/// mounted and running — this is a rendered layer above the current view,
/// not a navigation step, so capture sessions and room media pipelines
/// continue without interruption while Settings is open.
/// </summary>
public static class SettingsDialog
{
    public static void Show(ClientSettings settings, SettingsStore store, Action? onSaved = null)
    {
        if (Application.Current?.MainWindow?.DataContext is not INavigationHost nav)
        {
            return;
        }

        var vm = new SettingsViewModel(settings, store, onSaved);
        vm.CloseRequested += nav.CloseOverlay;
        nav.ShowOverlay(vm);
    }
}
