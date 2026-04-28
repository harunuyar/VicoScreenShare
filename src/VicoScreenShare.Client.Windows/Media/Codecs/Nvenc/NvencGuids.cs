namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using System;

/// <summary>
/// NVENC codec / profile / preset GUIDs, copied verbatim from
/// <c>nvEncodeAPI.h</c> (NVENC SDK 13.0). The SDK uses static const GUID
/// initializers; the values are stable across SDK versions and are part
/// of the published API contract — encoders identify themselves by these.
///
/// We only mirror the identifiers we actually use. Adding a new codec
/// (HEVC, AV1) means adding more entries here, not touching anything else.
/// </summary>
internal static class NvencGuids
{
    // Codec GUIDs — see nvEncodeAPI.h:144.
    public static readonly Guid CodecH264 = new(
        0x6bc82762, 0x4e63, 0x4ca4, 0xaa, 0x85, 0x1e, 0x50, 0xf3, 0x21, 0xf6, 0xbf);

    // H.264 profile GUIDs — see nvEncodeAPI.h:174.
    public static readonly Guid H264ProfileHigh = new(
        0xe7cbc309, 0x4f7a, 0x4b89, 0xaf, 0x2a, 0xd5, 0x37, 0xc9, 0x2b, 0xe3, 0x10);

    public static readonly Guid CodecProfileAutoselect = new(
        0xbfd6f8e7, 0x233c, 0x4341, 0x8b, 0x3e, 0x48, 0x18, 0x52, 0x38, 0x03, 0xf4);

    // Preset GUIDs P1..P7 — see nvEncodeAPI.h:226. P1 fastest, P7 best quality.
    // Per the SDK header (line 222): "Performance degrades and quality
    // improves as we move from P1 to P7." We default to P4 (mid) and let
    // a future readability mode push toward P5/P6.
    public static readonly Guid PresetP4 = new(
        0x90a7b826, 0xdf06, 0x4862, 0xb9, 0xd2, 0xcd, 0x6d, 0x73, 0xa0, 0x86, 0x81);
}
