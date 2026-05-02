namespace VicoScreenShare.Client.Windows.Direct3D;

using System;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

/// <summary>
/// GPU-only NV12 → BGRA converter that takes <em>two</em> separately
/// allocated D3D11 textures (Y as <c>R8_UNorm</c>, UV as <c>R8G8_UNorm</c>
/// at half-res) and writes a packed BGRA texture via a fullscreen-triangle
/// pixel shader.
///
/// The reason this class exists at all (rather than just using
/// <see cref="D3D11VideoScaler"/>): cuGraphicsD3D11RegisterResource won't
/// expose the UV plane of a <c>DXGI_FORMAT_NV12</c> texture to CUDA on
/// any driver we've tested — arrayIndex=1 returns
/// <c>CUDA_ERROR_INVALID_VALUE</c>, and the cuMemcpy2D-with-Height*1.5
/// trick from NVIDIA's NvDecodeD3D11 sample fails the same way (the
/// mapped CUarray is exactly Y rows tall). Splitting the destination
/// into two non-video-format textures sidesteps the limitation: R8 and
/// R8G8 textures have no special handling, CUDA-D3D11 interop maps both
/// cleanly, and a 30-line pixel shader recombines them into BGRA.
/// </summary>
public sealed class Nv12PlanesToBgraConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    /// <summary>
    /// HLSL: fullscreen-triangle vertex shader (no input layout — just
    /// SV_VertexID), and a pixel shader that samples Y at full-res and
    /// UV at half-res, then applies BT.709 limited-range matrix to
    /// produce BGRA. Limited-range matches what NVENC encodes by default
    /// for HD content; if NVDEC's color metadata says otherwise, we'd
    /// switch to BT.601 here. For 1080p+ NVENC output, BT.709 limited
    /// is correct.
    /// </summary>
    private const string ShaderSource = @"
Texture2D<float>  yTex  : register(t0);
Texture2D<float2> uvTex : register(t1);
SamplerState samp : register(s0);

struct VsOut {
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VsOut VsMain(uint id : SV_VertexID) {
    VsOut o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float4 PsMain(VsOut i) : SV_Target {
    float  y  = yTex.Sample(samp, i.uv).r;
    float2 uv = uvTex.Sample(samp, i.uv).rg;

    // BT.709 limited-range NV12 → linear RGB.
    float yL = (y       - 16.0/255.0) * (255.0/219.0);
    float u  = (uv.r    - 128.0/255.0) * (255.0/224.0);
    float v  = (uv.g    - 128.0/255.0) * (255.0/224.0);

    float r = yL + 1.5748 * v;
    float g = yL - 0.1873 * u - 0.4681 * v;
    float b = yL + 1.8556 * u;

    // SV_Target writes (R,G,B,A) regardless of render-target format —
    // the DXGI_FORMAT_B8G8R8A8_UNORM swap is done by the output merger,
    // not the shader. Returning float4(b,g,r,1) here would double-swap
    // and produce blue where red should be.
    return float4(saturate(r), saturate(g), saturate(b), 1.0);
}
";

    public Nv12PlanesToBgraConverter(ID3D11Device device, int width, int height)
    {
        _device = device;
        _context = device.ImmediateContext;
        _width = width;
        _height = height;

        // Compile vertex + pixel shader once. Throw on failure — caller
        // is expected to have a working D3DCompiler_47.dll on Windows;
        // the runtime ships with one in System32.
        var vsResult = Compiler.Compile(ShaderSource, "VsMain", "Nv12ToBgra.hlsl", "vs_4_0", out var vsBlob, out var vsError);
        if (vsResult.Failure)
        {
            throw new InvalidOperationException($"VS compile failed: {(vsError is not null ? System.Text.Encoding.ASCII.GetString(vsError.AsBytes()) : vsResult.Description)}");
        }
        var psResult = Compiler.Compile(ShaderSource, "PsMain", "Nv12ToBgra.hlsl", "ps_4_0", out var psBlob, out var psError);
        if (psResult.Failure)
        {
            vsBlob?.Dispose();
            throw new InvalidOperationException($"PS compile failed: {(psError is not null ? System.Text.Encoding.ASCII.GetString(psError.AsBytes()) : psResult.Description)}");
        }
        try
        {
            _vertexShader = device.CreateVertexShader(vsBlob.AsBytes());
            _pixelShader = device.CreatePixelShader(psBlob.AsBytes());
        }
        finally
        {
            vsBlob.Dispose();
            psBlob.Dispose();
            vsError?.Dispose();
            psError?.Dispose();
        }

        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });
    }

    /// <summary>
    /// One conversion pass. <paramref name="yTexture"/> is R8_UNorm at
    /// (width × height); <paramref name="uvTexture"/> is R8G8_UNorm at
    /// (width/2 × height/2). <paramref name="bgraDest"/> is the BGRA
    /// render target the result is drawn into.
    ///
    /// Caller is responsible for releasing the SRVs/RTV created here —
    /// we keep them inside the method since they're ID3D11View-typed
    /// and built from caller-owned textures we don't control the
    /// lifetime of.
    /// </summary>
    public void Convert(ID3D11Texture2D yTexture, ID3D11Texture2D uvTexture, ID3D11Texture2D bgraDest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var yView = _device.CreateShaderResourceView(yTexture, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });
        using var uvView = _device.CreateShaderResourceView(uvTexture, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });
        using var rtv = _device.CreateRenderTargetView(bgraDest, new RenderTargetViewDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            ViewDimension = RenderTargetViewDimension.Texture2D,
            Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
        });

        // No state save/restore: the WPF renderer rebinds its full
        // pipeline state every frame (D3DImageVideoRenderer creates its
        // own SRV from _bgraDest each tick), so leaving our shader /
        // viewport / OM state behind doesn't poison anyone. The capture
        // path runs on a separate thread but uses ID3D11Multithread for
        // serialization at the device level.
        _context.OMSetRenderTargets(rtv);
        _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetShaderResource(0, yView);
        _context.PSSetShaderResource(1, uvView);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);
        // Unbind RTV so subsequent SRV reads of bgraDest don't trigger
        // the D3D11 debug warning about a resource being bound as both
        // RT and SRV.
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        _context.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        _context.PSSetShaderResource(1, (ID3D11ShaderResourceView?)null);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _vertexShader.Dispose();
        _pixelShader.Dispose();
        _sampler.Dispose();
    }
}
