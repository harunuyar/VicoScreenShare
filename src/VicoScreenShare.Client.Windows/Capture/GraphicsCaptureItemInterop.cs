namespace VicoScreenShare.Client.Windows.Capture;

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using global::Windows.Graphics.Capture;
using VicoScreenShare.Client.Diagnostics;

/// <summary>
/// Materializes a <see cref="GraphicsCaptureItem"/> directly from a
/// known <c>HWND</c> or <c>HMONITOR</c>, bypassing the system picker.
/// <para>
/// Getting the <c>IGraphicsCaptureItemInterop</c> factory through
/// CsWinRT's managed <c>WinRT.ActivationFactory.Get</c> returns a
/// CCW around the managed projection — <c>QueryInterface</c> on that
/// CCW never reaches the native factory's interop interface and
/// returns <c>E_NOINTERFACE</c>. The correct entry point is
/// <c>RoGetActivationFactory</c> with the interop IID up front; the
/// Windows Runtime loader resolves to the native factory and QI's
/// for the requested interface in one step.
/// </para>
/// </summary>
internal static class GraphicsCaptureItemInterop
{
    // Interface IID (IGraphicsCaptureItemInterop).
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // IGraphicsCaptureItem IID — the WinRT-level interface the factory
    // hands us back; we then wrap it via the CsWinRT projection's
    // FromAbi. Well-known public IID; hardcoded so we don't depend on
    // CsWinRT's runtime-generated metadata path (which throws a
    // TypeInitializationException on some hosts when referenced from a
    // static field initializer).
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private const string RuntimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    public static async Task<GraphicsCaptureItem?> CreateForWindowAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        await Task.CompletedTask;
        return CreateForTarget(hwnd, vtableSlot: 3);
    }

    public static async Task<GraphicsCaptureItem?> CreateForMonitorAsync(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return null;
        }
        await Task.CompletedTask;
        return CreateForTarget(hMonitor, vtableSlot: 4);
    }

    private static GraphicsCaptureItem? CreateForTarget(IntPtr handle, int vtableSlot)
    {
        IntPtr hstring = IntPtr.Zero;
        IntPtr interopPtr = IntPtr.Zero;
        try
        {
            var hr = WindowsCreateString(RuntimeClassName, RuntimeClassName.Length, out hstring);
            if (hr != 0)
            {
                DebugLog.Write($"[picker] WindowsCreateString hr=0x{hr:X8}");
                return null;
            }

            var interopIid = IID_IGraphicsCaptureItemInterop;
            hr = RoGetActivationFactory(hstring, ref interopIid, out interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero)
            {
                DebugLog.Write($"[picker] RoGetActivationFactory hr=0x{hr:X8}");
                return null;
            }

            // Slot 3 = CreateForWindow(HWND, REFIID, void**)
            // Slot 4 = CreateForMonitor(HMONITOR, REFIID, void**)
            // Both share the same ABI signature — dispatch by slot index.
            var vtable = Marshal.ReadIntPtr(interopPtr);
            var fn = Marshal.ReadIntPtr(vtable, vtableSlot * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForHandleDelegate>(fn);

            var itemIid = IID_IGraphicsCaptureItem;
            var createHr = create(interopPtr, handle, ref itemIid, out var rawItem);
            if (createHr != 0 || rawItem == IntPtr.Zero)
            {
                DebugLog.Write($"[picker] CreateFor(slot={vtableSlot}, handle=0x{handle.ToInt64():X}) hr=0x{createHr:X8}");
                return null;
            }

            try
            {
                // FromAbi attaches to the existing AddRef — do NOT Release
                // the raw pointer here; the CsWinRT projection's
                // Dispose / finalizer will.
                return GraphicsCaptureItem.FromAbi(rawItem);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[picker] GraphicsCaptureItem.FromAbi threw: {ex.Message}");
                Marshal.Release(rawItem);
                return null;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[picker] CreateForTarget threw: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
            {
                DebugLog.Write($"[picker]   inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            return null;
        }
        finally
        {
            if (interopPtr != IntPtr.Zero) Marshal.Release(interopPtr);
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    // HRESULT CreateForWindow(HWND, REFIID, void**) /
    // HRESULT CreateForMonitor(HMONITOR, REFIID, void**)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForHandleDelegate(IntPtr @this, IntPtr handle, ref Guid riid, out IntPtr result);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);
}
