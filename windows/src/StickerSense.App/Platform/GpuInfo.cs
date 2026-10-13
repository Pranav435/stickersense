using System.Runtime.InteropServices;

namespace StickerSense.App.Platform;

/// <summary>
/// Asks DXGI which graphics adapters exist, to recommend the DirectML (graphics card) model or
/// the processor-only one. Talks to the COM vtable directly to avoid an extra dependency.
/// </summary>
internal static unsafe class GpuInfo
{
    public sealed record Adapter(string Name, uint VendorId, ulong DedicatedMemory, bool IsSoftware);

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const uint MicrosoftBasicRenderVendor = 0x1414;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    public static IReadOnlyList<Adapter> Adapters()
    {
        var adapters = new List<Adapter>();
        try
        {
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0 || factory == IntPtr.Zero) return adapters;
            try
            {
                // IDXGIFactory1::EnumAdapters1 is vtable slot 12 (IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5).
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
                for (uint i = 0; ; i++)
                {
                    IntPtr adapter;
                    var hr = enumAdapters1(factory, i, &adapter);
                    if (hr == DXGI_ERROR_NOT_FOUND || hr < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        // IDXGIAdapter1::GetDesc1 is slot 10 (IUnknown 3 + IDXGIObject 4 + IDXGIAdapter 3).
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)adapter)[10];
                        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_ADAPTER_DESC1>());
                        try
                        {
                            if (getDesc1(adapter, buffer) >= 0)
                            {
                                var desc = Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>(buffer);
                                adapters.Add(new Adapter(desc.Description, desc.VendorId, desc.DedicatedVideoMemory,
                                    (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || desc.VendorId == MicrosoftBasicRenderVendor));
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
        }
        return adapters;
    }

    /// <summary>A real (hardware) graphics adapter DirectML can use.</summary>
    public static Adapter? BestHardwareAdapter() =>
        Adapters().Where(a => !a.IsSoftware).OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();

    /// <summary>Graphics memory of its own that makes the graphics card model worth it.</summary>
    public const ulong MinimumDedicatedMemory = 2UL * 1024 * 1024 * 1024;

    /// <summary>
    /// The graphics card to recommend the DirectML model for: one with at least 2 GB of its own
    /// memory. Built-in graphics that share the computer's memory run the model far slower than the
    /// processor does (measured on Intel UHD Graphics 750 next to an i7-11700: about 100 seconds a
    /// sticker against about 10), so for those the processor model is recommended.
    /// </summary>
    public static Adapter? RecommendedAdapter() => BestHardwareAdapter() is { } best && IsWorthUsing(best) ? best : null;

    public static bool IsWorthUsing(Adapter adapter) => !adapter.IsSoftware && adapter.DedicatedMemory >= MinimumDedicatedMemory;
}
