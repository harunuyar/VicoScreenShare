namespace VicoScreenShare.Client.Platform;

using System;

/// <summary>
/// A shareable source enumerated from the OS — either a single window
/// or a whole monitor. Returned by <see cref="ICaptureTargetEnumerator"/>
/// and shown in the custom share picker. Everything the picker needs to
/// render a tile (display name, owner label, icon) is on this type;
/// thumbnails are fetched on demand via
/// <see cref="ICaptureTargetEnumerator.GetThumbnailAsync"/> because
/// capturing a live bitmap of every window up front would stall the
/// first paint on machines with dozens of windows open.
/// </summary>
public sealed class CaptureTarget
{
    public CaptureTarget(
        CaptureTargetKind kind,
        string displayName,
        string ownerDisplayName,
        IntPtr handle,
        int processId,
        CaptureTargetImage? icon)
    {
        Kind = kind;
        DisplayName = displayName ?? string.Empty;
        OwnerDisplayName = ownerDisplayName ?? string.Empty;
        Handle = handle;
        ProcessId = processId;
        Icon = icon;
    }

    /// <summary>Window or Monitor.</summary>
    public CaptureTargetKind Kind { get; }

    /// <summary>
    /// For windows, the window title (<c>GetWindowText</c>). For monitors,
    /// the friendly adapter name or "Display N".
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// For windows, the owning executable's friendly name (e.g. "chrome",
    /// "Visual Studio"). For monitors, the adapter vendor string. Shown
    /// as the picker tile's subtitle so the user can distinguish two
    /// windows that share a title.
    /// </summary>
    public string OwnerDisplayName { get; }

    /// <summary>
    /// Opaque OS handle — <c>HWND</c> for windows, <c>HMONITOR</c> for
    /// monitors. The platform layer consumes this to materialize a
    /// <c>GraphicsCaptureItem</c>; callers shouldn't interpret it. Zero
    /// on synthetic targets (fakes in tests).
    /// </summary>
    public IntPtr Handle { get; }

    /// <summary>
    /// Owning process id for window targets. Zero for monitor targets.
    /// The shared-audio pipeline uses this to attach a process-scoped
    /// loopback capture so a window share also carries the window's
    /// audio (not the whole system mix).
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// Small icon bitmap (typically 16×16 or 32×32 BGRA) for tile
    /// decoration. Null if the target has no icon (some system windows
    /// and most monitors). The platform implementation pre-materializes
    /// this during enumeration because icons are cheap and drive the
    /// picker's at-a-glance recognition.
    /// </summary>
    public CaptureTargetImage? Icon { get; }

    public override string ToString() => $"{Kind}: {DisplayName} ({OwnerDisplayName})";
}

public enum CaptureTargetKind
{
    Window = 0,
    Monitor = 1,
}

/// <summary>
/// Raw BGRA pixel buffer suitable for binding to any WPF /
/// cross-platform image surface. Platform-neutral so tests can
/// construct synthetic icons without referencing PresentationCore.
/// </summary>
public sealed class CaptureTargetImage
{
    public CaptureTargetImage(byte[] bgraPixels, int width, int height, int strideBytes)
    {
        BgraPixels = bgraPixels ?? throw new ArgumentNullException(nameof(bgraPixels));
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
    }

    public byte[] BgraPixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
}
