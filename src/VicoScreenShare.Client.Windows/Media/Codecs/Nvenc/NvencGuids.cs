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
    // the user move toward P5/P6/P7 via Settings if encode-time budget allows.
    public static readonly Guid PresetP1 = new(
        0xfc0a8d3e, 0x45f8, 0x4cf8, 0x80, 0xc7, 0x29, 0x88, 0x71, 0x59, 0x0e, 0xbf);
    public static readonly Guid PresetP2 = new(
        0xf581cfb8, 0x88d6, 0x4381, 0x93, 0xf0, 0xdf, 0x13, 0xf9, 0xc2, 0x7d, 0xab);
    public static readonly Guid PresetP3 = new(
        0x36850110, 0x3a07, 0x441f, 0x94, 0xd5, 0x36, 0x70, 0x63, 0x1f, 0x91, 0xf6);
    public static readonly Guid PresetP4 = new(
        0x90a7b826, 0xdf06, 0x4862, 0xb9, 0xd2, 0xcd, 0x6d, 0x73, 0xa0, 0x86, 0x81);
    public static readonly Guid PresetP5 = new(
        0x21c6e6b4, 0x297a, 0x4cba, 0x99, 0x8f, 0xb6, 0xcb, 0xde, 0x72, 0xad, 0xe3);
    public static readonly Guid PresetP6 = new(
        0x8e75c279, 0x6299, 0x4ab6, 0x83, 0x02, 0x0b, 0x21, 0x5a, 0x33, 0x5c, 0xf5);
    public static readonly Guid PresetP7 = new(
        0x84848c12, 0x6f71, 0x4c13, 0x93, 0x1b, 0x53, 0xe2, 0x83, 0xf5, 0x79, 0x74);

    /// <summary>Map a 1..7 preset selector to its NVENC GUID. Out-of-range
    /// values clamp to P4 (the safe mid default).</summary>
    public static Guid PresetByLevel(int level) => level switch
    {
        1 => PresetP1,
        2 => PresetP2,
        3 => PresetP3,
        5 => PresetP5,
        6 => PresetP6,
        7 => PresetP7,
        _ => PresetP4,
    };
}
