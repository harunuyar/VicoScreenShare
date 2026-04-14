using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
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
/// Maps a view model to the matching <see cref="System.Windows.Controls.UserControl"/>.
/// Used by <c>MainWindow.xaml</c>'s ContentControl so the shell can render
/// whichever VM <see cref="NavigationService"/> currently points at.
/// </summary>
public sealed class ViewModelToPageConverter : IValueConverter
{
    private static readonly Dictionary<Type, Func<object, System.Windows.FrameworkElement>> Map = new()
    {
        [typeof(HomeViewModel)] = vm => new HomeView { DataContext = vm },
        [typeof(RoomViewModel)] = vm => new RoomView { DataContext = vm },
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
