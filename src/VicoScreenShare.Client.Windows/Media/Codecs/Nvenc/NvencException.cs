namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using System;

/// <summary>
/// All NVENC SDK calls return <see cref="NVENCSTATUS"/>. Anything other than
/// <see cref="NVENCSTATUS.NV_ENC_SUCCESS"/> is wrapped in this exception so
/// the encoder layer can fail-fast and the factory selector can catch and
/// fall back to the MFT path. <see cref="LastErrorString"/> carries the
/// driver-side detail string returned by <c>nvEncGetLastErrorString</c>
/// when one is available — the bare status code is often not enough.
/// </summary>
public sealed class NvencException : Exception
{
    public NVENCSTATUS Status { get; }

    public string? LastErrorString { get; }

    public NvencException(NVENCSTATUS status, string operation, string? lastErrorString = null)
        : base(BuildMessage(status, operation, lastErrorString))
    {
        Status = status;
        LastErrorString = lastErrorString;
    }

    private static string BuildMessage(NVENCSTATUS status, string operation, string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return $"NVENC {operation} failed with {status}";
        }
        return $"NVENC {operation} failed with {status}: {detail}";
    }
}
