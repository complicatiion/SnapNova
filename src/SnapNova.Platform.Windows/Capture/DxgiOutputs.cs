using System.Runtime.InteropServices;

namespace SnapNova.Platform.Windows.Capture;

/// <summary>
/// Maps a monitor (device name, e.g. \\.\DISPLAY2) to the DXGI output index of the default adapter – the index FFmpeg's
/// ddagrab expects. Monitors on a second GPU return null (the recorder then falls back to gdigrab).
/// Plain COM vtable calls: no extra package for three calls.
/// </summary>
public static unsafe class DxgiOutputs
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    // vtable slots: IUnknown 0–2, IDXGIObject 3–6, IDXGIFactory 7–11, IDXGIFactory1 12–13
    private const int FactoryEnumAdapters1 = 12;

    // IDXGIAdapter: 7 = EnumOutputs
    private const int AdapterEnumOutputs = 7;

    // IDXGIOutput: 7 = GetDesc
    private const int OutputGetDesc = 7;
    private const int Release = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawOutputDesc
    {
        public fixed char DeviceName[32];
        public Native.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    /// <summary>Output index on adapter 0 for the monitor, or null.</summary>
    public static int? OutputIndexOf(string deviceName)
    {
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(IidFactory1, out factory) < 0 || factory == IntPtr.Zero)
            {
                return null;
            }

            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(factory, FactoryEnumAdapters1);
            IntPtr a;
            if (enumAdapters(factory, 0, &a) < 0)
            {
                return null;
            }

            adapter = a;
            var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(adapter, AdapterEnumOutputs);
            for (uint i = 0; ; i++)
            {
                IntPtr output;
                var hr = enumOutputs(adapter, i, &output);
                if (hr == DxgiErrorNotFound || hr < 0)
                {
                    return null;
                }

                try
                {
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, RawOutputDesc*, int>)VTable(output, OutputGetDesc);
                    RawOutputDesc desc;
                    if (getDesc(output, &desc) >= 0)
                    {
                        // Fixed WCHAR[32], zero-terminated
                        var name = new string(desc.DeviceName);

                        if (string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            return (int)i;
                        }
                    }
                }
                finally
                {
                    ReleaseCom(output);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }
        finally
        {
            ReleaseCom(adapter);
            ReleaseCom(factory);
        }
    }

    private static IntPtr VTable(IntPtr comObject, int slot)
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void ReleaseCom(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)VTable(comObject, Release))(comObject);
        }
    }
}
