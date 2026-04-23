namespace VicoScreenShare.Desktop.App.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

/// <summary>
/// Speaker button with a hover-activated volume slider that opens below
/// the button with a fade animation and auto-closes after a short
/// timeout once the mouse leaves both the button and the slider.
/// <para>
/// The Popup's own <c>IsMouseOver</c> would be cleaner but doesn't
/// transition predictably across the gap between the parent element
/// and the popup window (popups are their own HWND). A dispatcher
/// timer that arms on leave and disarms on re-enter handles the gap
/// reliably with a short grace window.
/// </para>
/// </summary>
public partial class VolumeFlyoutButton : UserControl
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _closeTimer;

    public VolumeFlyoutButton()
    {
        InitializeComponent();
        _closeTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = CloseDelay };
        _closeTimer.Tick += OnCloseTimerTick;
        Unloaded += (_, _) => _closeTimer.Stop();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VolumeFlyoutButton),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(VolumeFlyoutButton),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public static readonly DependencyProperty ToggleMuteCommandProperty = DependencyProperty.Register(
        nameof(ToggleMuteCommand), typeof(ICommand), typeof(VolumeFlyoutButton),
        new PropertyMetadata(null));

    public ICommand? ToggleMuteCommand
    {
        get => (ICommand?)GetValue(ToggleMuteCommandProperty);
        set => SetValue(ToggleMuteCommandProperty, value);
    }

    private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
    {
        _closeTimer.Stop();
        Flyout.IsOpen = true;
    }

    private void OnAnchorMouseLeave(object sender, MouseEventArgs e) => _closeTimer.Start();

    private void OnFlyoutMouseEnter(object sender, MouseEventArgs e) => _closeTimer.Stop();

    private void OnFlyoutMouseLeave(object sender, MouseEventArgs e) => _closeTimer.Start();

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        Flyout.IsOpen = false;
    }
}
