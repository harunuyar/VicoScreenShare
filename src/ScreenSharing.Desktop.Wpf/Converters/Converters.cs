using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ScreenSharing.Desktop.App.ViewModels;
using ScreenSharing.Desktop.App.Views;

namespace ScreenSharing.Desktop.App.Converters;

/// <summary>
/// Small set of value converters the XAML needs. Kept in one file.
/// </summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrEmpty(s))
            ? Visibility.Collapsed
            : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when the source is non-null, false otherwise. Used to drive a
/// <c>Popup.IsOpen</c> from a nullable view-model reference.
/// </summary>
public sealed class NonNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not null && !(value is string s && string.IsNullOrEmpty(s));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <c>Visible</c> when the bound <see cref="ViewModels.RoomLayout"/> value
/// matches the <c>ConverterParameter</c> (the enum member name as a string),
/// <c>Collapsed</c> otherwise. Lets the Room view mount two layout templates
/// side by side and toggle between them without a visual-tree churn.
/// </summary>
public sealed class LayoutToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}

/// <summary>
/// Tile-count → UniformGrid columns/rows. One converter per dimension so a
/// single Int32 source binding drives both <c>Columns</c> and <c>Rows</c>.
/// Layout rules: 1→1x1, 2→1x2, 3-4→2x2, 5-9→3x3, 10+→ceil(N/4) rows × 4 cols.
/// </summary>
public sealed class TileLayoutColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int n ? Math.Max(1, n) : 1;
        return count switch
        {
            1 => 1,
            2 => 2,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4,
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Tooltip for a peer chip: null (no tooltip) when connected, a friendly
/// "reconnecting" line while the peer is in the server's grace window.
/// </summary>
public sealed class PeerConnectionTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && !b)
        {
            return "Reconnecting…";
        }
        return null;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TileLayoutRowsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int n ? Math.Max(1, n) : 1;
        return count switch
        {
            1 => 1,
            2 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => (count + 3) / 4,
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a view model to the matching <see cref="System.Windows.Controls.UserControl"/>.
/// Used by <c>MainWindow.xaml</c>'s ContentControl so the shell can render
/// whichever VM <see cref="NavigationService"/> currently points at.
/// </summary>
/// <summary>
/// <see cref="ServerStatus"/> → status-dot <see cref="Brush"/>. Green for
/// online, yellow for auth-required, red for offline, gray while checking
/// or unknown. Picker rows bind the dot's Fill to this.
/// </summary>
public sealed class ServerStatusToBrushConverter : IValueConverter
{
    private static readonly Brush Online = new SolidColorBrush(Color.FromRgb(0x3E, 0xC9, 0x4B));
    private static readonly Brush AuthRequired = new SolidColorBrush(Color.FromRgb(0xF5, 0xB1, 0x2C));
    private static readonly Brush Offline = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F));
    private static readonly Brush Pending = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    static ServerStatusToBrushConverter()
    {
        Online.Freeze();
        AuthRequired.Freeze();
        Offline.Freeze();
        Pending.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ScreenSharing.Client.Services.ServerStatus status) return Pending;
        return status switch
        {
            ScreenSharing.Client.Services.ServerStatus.Online => Online,
            ScreenSharing.Client.Services.ServerStatus.AuthRequired => AuthRequired,
            ScreenSharing.Client.Services.ServerStatus.Offline => Offline,
            _ => Pending,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <see cref="ServerStatus"/> → a short human label: "Online",
/// "Password required", "Offline", "Checking…", or "—" for Unknown.
/// </summary>
public sealed class ServerStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ScreenSharing.Client.Services.ServerStatus status) return "—";
        return status switch
        {
            ScreenSharing.Client.Services.ServerStatus.Online => "Online",
            ScreenSharing.Client.Services.ServerStatus.AuthRequired => "Password required",
            ScreenSharing.Client.Services.ServerStatus.Offline => "Offline",
            ScreenSharing.Client.Services.ServerStatus.Checking => "Checking…",
            _ => "—",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ViewModelToPageConverter : IValueConverter
{
    private static readonly Dictionary<Type, Func<object, System.Windows.FrameworkElement>> Map = new()
    {
        [typeof(HomeViewModel)] = vm => new HomeView { DataContext = vm },
        [typeof(RoomViewModel)] = vm => new RoomView { DataContext = vm },
        [typeof(CaptureTestViewModel)] = vm => new CaptureTestView { DataContext = vm },
        [typeof(SettingsViewModel)] = vm => new SettingsView { DataContext = vm },
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return null;
        return Map.TryGetValue(value.GetType(), out var factory) ? factory(value) : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
