namespace VicoScreenShare.Client.Windows.Direct3D;

using System;
using System.Runtime.InteropServices;
using System.Text;
using VicoScreenShare.Client.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// GPU-side BGRA → BGRA downscaler using a separable Lanczos3 compute
/// shader. Produces significantly sharper text than the bilinear filter
/// the D3D11 Video Processor uses, at the cost of more GPU work per
/// frame. Intended for "Readable" / code-session quality where text
/// clarity matters more than frame rate.
///
/// Two-pass separable approach: horizontal pass (src → intermediate at
/// dstWidth × srcHeight), then vertical pass (intermediate → dest at
/// dstWidth × dstHeight). Drops per-pixel tap count from 169 to 26
/// for a Lanczos3 kernel.
///
/// When source and destination dimensions are equal, degenerates to a
/// <see cref="ID3D11DeviceContext.CopyResource"/> — no shader work.
/// </summary>
public sealed class LanczosScaler : ITextureScaler
{
    private readonly ID3D11Device _device;
    private readonly ID3D11ComputeShader _csHorizontal;
    private readonly ID3D11ComputeShader _csVertical;
    private readonly ID3D11Buffer _cbParams;
    private readonly ID3D11Texture2D _intermediate;
    private readonly ID3D11ShaderResourceView _intermediateSrv;
    private readonly ID3D11UnorderedAccessView _intermediateUav;
    private bool _disposed;

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int DestWidth { get; }
    public int DestHeight { get; }

    public LanczosScaler(ID3D11Device device, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        _device = device;
        SourceWidth = srcWidth;
        SourceHeight = srcHeight;
        DestWidth = dstWidth;
        DestHeight = dstHeight;

        // Compile shaders from embedded HLSL.
        _csHorizontal = CompileAndCreate(device, ShaderSource, "CSHorizontal");
        _csVertical = CompileAndCreate(device, ShaderSource, "CSVertical");

        // Constant buffer: 4 uints = 16 bytes.
        var cbDesc = new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default);
        _cbParams = device.CreateBuffer(cbDesc);

        // Intermediate texture: dstWidth × srcHeight (horizontal pass output).
        var intDesc = new Texture2DDescription
        {
            Width = (uint)dstWidth,
            Height = (uint)srcHeight,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        };
        _intermediate = device.CreateTexture2D(intDesc);
        _intermediateSrv = device.CreateShaderResourceView(_intermediate);
        _intermediateUav = device.CreateUnorderedAccessView(_intermediate);

        DebugLog.Write($"[lanczos] scaler built {srcWidth}x{srcHeight} -> {dstWidth}x{dstHeight}");
    }

    public void Process(ID3D11Texture2D source, ID3D11Texture2D dest)
    {
        if (_disposed) return;

        // No-op when dimensions match.
        if (SourceWidth == DestWidth && SourceHeight == DestHeight)
        {
            _device.ImmediateContext.CopyResource(dest, source);
            return;
        }

        var ctx = _device.ImmediateContext;

        // Upload constant buffer once — both passes read from the same
        // four values. The horizontal shader uses SrcWidth/DstWidth for
        // its ratio; the vertical shader uses SrcHeight/DstHeight.
        var cbData = new ScaleParams
        {
            SrcWidth = (uint)SourceWidth,
            SrcHeight = (uint)SourceHeight,
            DstWidth = (uint)DestWidth,
            DstHeight = (uint)DestHeight,
        };
        ctx.UpdateSubresource(cbData, _cbParams);

        // --- Pass 1: Horizontal (src → intermediate) ---
        using var srcSrv = _device.CreateShaderResourceView(source);

        ctx.CSSetShader(_csHorizontal);
        ctx.CSSetConstantBuffer(0, _cbParams);
        ctx.CSSetShaderResource(0, srcSrv);
        ctx.CSSetUnorderedAccessView(0, _intermediateUav);
        ctx.Dispatch((uint)CeilDiv(DestWidth, 16), (uint)CeilDiv(SourceHeight, 16), 1);

        // Unbind to avoid SRV/UAV hazard between passes.
        ctx.CSSetShaderResource(0, null);
        ctx.CSSetUnorderedAccessView(0, null);

        // --- Pass 2: Vertical (intermediate → dest) ---
        using var destUav = _device.CreateUnorderedAccessView(dest);

        ctx.CSSetShader(_csVertical);
        ctx.CSSetShaderResource(0, _intermediateSrv);
        ctx.CSSetUnorderedAccessView(0, destUav);
        ctx.Dispatch((uint)CeilDiv(DestWidth, 16), (uint)CeilDiv(DestHeight, 16), 1);

        // Unbind.
        ctx.CSSetShaderResource(0, null);
        ctx.CSSetUnorderedAccessView(0, null);
        ctx.CSSetShader(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intermediateUav.Dispose();
        _intermediateSrv.Dispose();
        _intermediate.Dispose();
        _cbParams.Dispose();
        _csVertical.Dispose();
        _csHorizontal.Dispose();
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        byte[] pSrcData, nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
        IntPtr pDefines, IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string pEntryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint flags1, uint flags2,
        out IntPtr ppCode, out IntPtr ppErrorMsgs);

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DGetBlobPart(
        IntPtr pSrcData, nuint srcDataSize,
        int part, uint flags, out IntPtr ppPart);

    private static unsafe ID3D11ComputeShader CompileAndCreate(ID3D11Device device, string hlsl, string entryPoint)
    {
        var srcBytes = Encoding.UTF8.GetBytes(hlsl);
        var hr = D3DCompile(
            srcBytes, (nuint)srcBytes.Length,
            null, IntPtr.Zero, IntPtr.Zero,
            entryPoint, "cs_5_0",
            0, 0,
            out var codeBlob, out var errorBlob);

        if (hr < 0)
        {
            var msg = "unknown";
            if (errorBlob != IntPtr.Zero)
            {
                var bufPtr = Marshal.ReadIntPtr(errorBlob + IntPtr.Size * 0); // ID3DBlob::GetBufferPointer is vtable[3]
                // Simpler: use the Blob interface directly
                msg = "HRESULT 0x" + hr.ToString("X8");
            }
            DebugLog.Write($"[lanczos] shader compile failed for {entryPoint}: {msg}");
            throw new InvalidOperationException($"Lanczos shader compilation failed for {entryPoint}: {msg}");
        }

        // Extract bytecode from the ID3DBlob COM object.
        // ID3DBlob vtable: [QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize]
        var blobVtable = Marshal.ReadIntPtr(codeBlob);
        var getBufferPointerFn = Marshal.ReadIntPtr(blobVtable + IntPtr.Size * 3);
        var getBufferSizeFn = Marshal.ReadIntPtr(blobVtable + IntPtr.Size * 4);

        var bufferPtr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)getBufferPointerFn)(codeBlob);
        var bufferSize = ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)getBufferSizeFn)(codeBlob);

        var bytecode = new byte[bufferSize];
        Marshal.Copy(bufferPtr, bytecode, 0, (int)bufferSize);

        // Release the blobs.
        Marshal.Release(codeBlob);
        if (errorBlob != IntPtr.Zero) Marshal.Release(errorBlob);

        return device.CreateComputeShader(bytecode);
    }

    private struct ScaleParams
    {
        public uint SrcWidth;
        public uint SrcHeight;
        public uint DstWidth;
        public uint DstHeight;
    }

    // -----------------------------------------------------------------
    // Embedded HLSL — separable Lanczos3 compute shader
    // -----------------------------------------------------------------
    private const string ShaderSource = @"
cbuffer ScaleParams : register(b0)
{
    uint SrcWidth;
    uint SrcHeight;
    uint DstWidth;
    uint DstHeight;
};

Texture2D<float4> Input : register(t0);
RWTexture2D<float4> Output : register(u0);

static const float PI = 3.14159265358979323846;
static const int A = 3; // Lanczos support radius

float lanczos(float x)
{
    if (abs(x) < 1e-6) return 1.0;
    if (abs(x) >= A) return 0.0;
    float px = PI * x;
    return (sin(px) / px) * (sin(px / A) / (px / A));
}

// --- Horizontal pass: reads Input (SrcWidth x SrcHeight),
//     writes Output (DstWidth x SrcHeight). ---
[numthreads(16, 16, 1)]
void CSHorizontal(uint3 dtid : SV_DispatchThreadID)
{
    if (dtid.x >= DstWidth || dtid.y >= SrcHeight) return;

    float ratio = float(SrcWidth) / float(DstWidth);
    float center = (dtid.x + 0.5) * ratio - 0.5;
    float support = max(ratio, 1.0) * A;

    int iStart = max(0, (int)floor(center - support));
    int iEnd   = min((int)SrcWidth - 1, (int)ceil(center + support));

    float4 sum = 0;
    float wSum = 0;
    for (int i = iStart; i <= iEnd; i++)
    {
        float dist = (i - center) / max(ratio, 1.0);
        float w = lanczos(dist);
        sum += Input.Load(int3(i, dtid.y, 0)) * w;
        wSum += w;
    }
    Output[uint2(dtid.x, dtid.y)] = sum / max(wSum, 1e-6);
}

// --- Vertical pass: reads Input (DstWidth x SrcHeight),
//     writes Output (DstWidth x DstHeight). ---
[numthreads(16, 16, 1)]
void CSVertical(uint3 dtid : SV_DispatchThreadID)
{
    if (dtid.x >= DstWidth || dtid.y >= DstHeight) return;

    float ratio = float(SrcHeight) / float(DstHeight);
    float center = (dtid.y + 0.5) * ratio - 0.5;
    float support = max(ratio, 1.0) * A;

    int jStart = max(0, (int)floor(center - support));
    int jEnd   = min((int)SrcHeight - 1, (int)ceil(center + support));

    float4 sum = 0;
    float wSum = 0;
    for (int j = jStart; j <= jEnd; j++)
    {
        float dist = (j - center) / max(ratio, 1.0);
        float w = lanczos(dist);
        sum += Input.Load(int3(dtid.x, j, 0)) * w;
        wSum += w;
    }
    Output[uint2(dtid.x, dtid.y)] = sum / max(wSum, 1e-6);
}
";
}
