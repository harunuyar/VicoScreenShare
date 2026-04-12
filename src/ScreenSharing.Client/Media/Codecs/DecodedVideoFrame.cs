namespace ScreenSharing.Client.Media.Codecs;

/// <summary>
/// One decoded frame as a packed BGRA byte buffer. Decoder implementations
/// normalize whatever native pixel layout they get (BGR, NV12, I420, …) into
/// BGRA here so <see cref="StreamReceiver"/> can hand the bytes to Avalonia
/// without another conversion pass.
/// </summary>
public readonly record struct DecodedVideoFrame(byte[] Bgra, int Width, int Height);
