namespace VicoScreenShare.Desktop.App.ViewModels;

using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Platform;

/// <summary>
/// View-model wrapper around a <see cref="CaptureTarget"/> so the share
/// picker can bind tile UI (app icon + live preview + selection state)
/// without leaking WPF types into the platform-neutral layer.
/// <para>
/// The live preview is driven by a per-tile <see cref="ICaptureSource"/>
/// — the same WGC-backed source the room view uses for its self-preview
/// via <c>D3DImageVideoRenderer.LocalPreviewSource</c>. Reusing the
/// proven capture + render pipeline means the picker's preview renders
/// the same frames the user will actually share when they click that
/// tile.
/// </para>
/// </summary>
public sealed partial class CaptureTargetTileViewModel : ObservableObject
{
    public CaptureTargetTileViewModel(CaptureTarget target)
    {
        Target = target;
        _iconSource = ToBitmapSource(target.Icon);
    }

    public CaptureTarget Target { get; }

    public CaptureTargetKind Kind => Target.Kind;

    public bool IsWindow => Target.Kind == CaptureTargetKind.Window;

    public bool IsMonitor => Target.Kind == CaptureTargetKind.Monitor;

    public string DisplayName => Target.DisplayName;

    public string OwnerDisplayName => Target.OwnerDisplayName;

    [ObservableProperty]
    private ImageSource? _iconSource;

    /// <summary>Bound by the tile template's selection-state
    /// DataTrigger so the selected tile's border snaps to the accent
    /// color. Managed centrally by
    /// <see cref="SharePickerViewModel.SelectTile"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Per-tile WGC capture source that drives the live preview. Bound
    /// to <c>D3DImageVideoRenderer.LocalPreviewSource</c> by the tile
    /// template. Null until the picker attaches one; null again after
    /// the picker disposes.
    /// </summary>
    [ObservableProperty]
    private ICaptureSource? _captureSource;

    /// <summary>
    /// True while waiting for the first frame from <see cref="CaptureSource"/>.
    /// The spinner in the tile template is bound to this so the user
    /// has something to look at while WGC builds its framepool.
    /// </summary>
    [ObservableProperty]
    private bool _thumbnailLoading = true;

    /// <summary>
    /// Attach the per-tile capture source that renders the live
    /// preview. Wires a first-frame hook so the spinner hides as soon
    /// as pixels land; the picker orchestrator owns the source's
    /// lifecycle and disposes it when the dialog closes.
    /// </summary>
    public void AttachCaptureSource(ICaptureSource source)
    {
        if (CaptureSource is not null)
        {
            DetachFirstFrameHook(CaptureSource);
        }
        CaptureSource = source;
        if (source is not null)
        {
            AttachFirstFrameHook(source);
        }
    }

    /// <summary>
    /// Drop the capture source reference without disposing it — the
    /// picker VM owns disposal. Called when the picker closes.
    /// </summary>
    public void ClearCaptureSource()
    {
        if (CaptureSource is not null)
        {
            DetachFirstFrameHook(CaptureSource);
        }
        CaptureSource = null;
    }

    private void AttachFirstFrameHook(ICaptureSource source)
    {
        source.FrameArrived += OnFirstFrame;
        source.TextureArrived += OnFirstTexture;
    }

    private void DetachFirstFrameHook(ICaptureSource source)
    {
        source.FrameArrived -= OnFirstFrame;
        source.TextureArrived -= OnFirstTexture;
    }

    private void OnFirstFrame(in CaptureFrameData frame)
    {
        if (!ThumbnailLoading)
        {
            return;
        }
        // Marshal back to UI thread: CaptureSource events can fire from
        // any thread the capture backend uses.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => ThumbnailLoading = false));
    }

    private void OnFirstTexture(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
    {
        if (!ThumbnailLoading)
        {
            return;
        }
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => ThumbnailLoading = false));
    }

    private static ImageSource? ToBitmapSource(CaptureTargetImage? image)
    {
        if (image is null)
        {
            return null;
        }
        try
        {
            var bmp = BitmapSource.Create(
                image.Width,
                image.Height,
                96, 96,
                PixelFormats.Bgra32,
                palette: null,
                image.BgraPixels,
                image.StrideBytes);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[picker] icon BitmapSource.Create threw: {ex.Message}");
            return null;
        }
    }
}
