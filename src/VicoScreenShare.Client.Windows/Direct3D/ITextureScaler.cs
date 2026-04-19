namespace VicoScreenShare.Client.Windows.Direct3D;

using System;
using Vortice.Direct3D11;

/// <summary>
/// Common interface for GPU-side texture scalers. Both
/// <see cref="D3D11VideoScaler"/> (Video Processor, bilinear) and
/// <see cref="LanczosScaler"/> (compute shader, sharp text) implement
/// this so the encoder can hold either behind one field.
/// </summary>
public interface ITextureScaler : IDisposable
{
    int SourceWidth { get; }
    int SourceHeight { get; }
    int DestWidth { get; }
    int DestHeight { get; }

    void Process(ID3D11Texture2D source, ID3D11Texture2D dest);
}
