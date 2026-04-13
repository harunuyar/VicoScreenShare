using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ScreenSharing.Client.ViewModels;

namespace ScreenSharing.Client.Views;

public partial class RoomView : UserControl
{
    public RoomView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnCopyRoomIdClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoomViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(vm.RoomId);
            vm.OnRoomIdCopied();
        }
        catch
        {
            // Clipboard access can fail if another app is holding the clipboard —
            // not fatal, just swallow.
        }
    }
}
