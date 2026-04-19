namespace VicoScreenShare.Desktop.App.Behaviors;

using System;
using System.Windows;
using System.Windows.Controls.Primitives;

/// <summary>
/// Attached property that makes a WPF <see cref="Popup"/> follow its host
/// <see cref="Window"/> when the window moves or resizes.
///
/// Background: a <see cref="Popup"/> with <c>AllowsTransparency="True"</c>
/// is implemented as its own top-level HWND so it can sit above HwndHost
/// children (video renderers etc.) without airspace issues. The side-effect
/// is that WPF computes the popup's position once, at open time, from its
/// <see cref="Popup.PlacementTarget"/>, and does not re-evaluate when the
/// owning window is dragged or resized — the popup stays where the screen
/// was when it opened, visually detaching from the window.
///
/// The canonical workaround is to "nudge" <see cref="Popup.HorizontalOffset"/>:
/// setting any new value forces <c>PopupRoot</c> to recompute the screen
/// position from <c>PlacementTarget</c>. We set it to <c>offset + epsilon</c>
/// then back to <c>offset</c> so the end state is unchanged. This is the
/// approach documented across Stack Overflow and used by MahApps / Wpf-UI
/// themselves for the same problem.
///
/// Usage:
/// <code>
/// xmlns:b="clr-namespace:VicoScreenShare.Desktop.App.Behaviors"
/// &lt;Popup b:PopupFollowsWindow.IsEnabled="True" ...&gt;
/// </code>
/// </summary>
public static class PopupFollowsWindow
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(PopupFollowsWindow),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty HostWindowProperty =
        DependencyProperty.RegisterAttached(
            "HostWindow",
            typeof(Window),
            typeof(PopupFollowsWindow),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            popup.Opened += OnPopupOpened;
            popup.Closed += OnPopupClosed;
        }
        else
        {
            popup.Opened -= OnPopupOpened;
            popup.Closed -= OnPopupClosed;
            Detach(popup);
        }
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup)
        {
            return;
        }
        // PlacementTarget may live in a UserControl that's nested inside the
        // main Window; walk up the visual tree via Window.GetWindow to find
        // the owning top-level. Fall back to Application.Current.MainWindow
        // if the target isn't in the visual tree yet.
        var host = popup.PlacementTarget is DependencyObject target
            ? Window.GetWindow(target)
            : null;
        host ??= Application.Current?.MainWindow;
        if (host is null)
        {
            return;
        }

        popup.SetValue(HostWindowProperty, host);
        host.LocationChanged += OnHostChanged;
        host.SizeChanged += OnHostSizeChanged;
    }

    private static void OnPopupClosed(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
        {
            Detach(popup);
        }
    }

    private static void Detach(Popup popup)
    {
        if (popup.GetValue(HostWindowProperty) is Window host)
        {
            host.LocationChanged -= OnHostChanged;
            host.SizeChanged -= OnHostSizeChanged;
            popup.ClearValue(HostWindowProperty);
        }
    }

    private static void OnHostChanged(object? sender, EventArgs e) => NudgeOpenPopups(sender as Window);
    private static void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => NudgeOpenPopups(sender as Window);

    private static void NudgeOpenPopups(Window? host)
    {
        if (host is null)
        {
            return;
        }
        // We don't keep a registry of popups; instead we walk every Popup
        // currently associated with this host via the HostWindow attached
        // property. Cheap for our app — at most a couple of popups exist at
        // any moment — and avoids a global subscription list that could leak.
        foreach (var popup in FindAttachedPopups(host))
        {
            if (!popup.IsOpen)
            {
                continue;
            }
            var offset = popup.HorizontalOffset;
            // Floating-point delta is enough to flip PopupRoot's dirty flag
            // and trigger a position recompute, without shifting the popup
            // visually.
            popup.HorizontalOffset = offset + 0.01;
            popup.HorizontalOffset = offset;
        }
    }

    private static System.Collections.Generic.IEnumerable<Popup> FindAttachedPopups(Window host)
    {
        // Enumerate the window's logical children recursively, yielding any
        // Popup whose HostWindow attached value matches this host. Popups
        // live in the logical tree even when their visual root is detached
        // (PopupRoot).
        var stack = new System.Collections.Generic.Stack<object>();
        stack.Push(host);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is Popup p && p.GetValue(HostWindowProperty) == host)
            {
                yield return p;
            }
            if (current is DependencyObject dep)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(dep))
                {
                    stack.Push(child);
                }
            }
        }
    }
}
