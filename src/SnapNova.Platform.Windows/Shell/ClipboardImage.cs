using System.Runtime.InteropServices;
using SnapNova.Core.Capture;
using static SnapNova.Platform.Windows.Native;

namespace SnapNova.Platform.Windows.Shell;

/// <summary>
/// Puts an image on the clipboard as CF_DIB (every app) and as "PNG" (Office, browsers, chat apps keep transparency).
/// Uses its own message-only window as clipboard owner: with a NULL owner, SetClipboardData fails after EmptyClipboard.
/// </summary>
public static class ClipboardImage
{
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;

    /// <param name="excludeFromHistory">Keeps the content out of the Windows clipboard history (Win+V) and the cloud clipboard.</param>
    public static bool Set(PixelImage image, byte[]? png, bool excludeFromHistory = false) =>
        Write(excludeFromHistory, () =>
        {
            var ok = SetGlobal(CF_DIB, Dib(image));
            if (png is not null)
            {
                ok |= SetGlobal(RegisterClipboardFormat("PNG"), png);
            }

            return ok;
        });

    /// <summary>Several captures as files (paste into Explorer, Teams, Outlook …).</summary>
    public static bool SetFiles(IReadOnlyList<string> paths, bool excludeFromHistory = false)
    {
        // DROPFILES (20 bytes) + double-zero-terminated UTF-16 path list.
        var list = System.Text.Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);   // pFiles
        BitConverter.GetBytes(1).CopyTo(data, 16);   // fWide
        list.CopyTo(data, 20);
        return Write(excludeFromHistory, () => SetGlobal(CfHdrop, data));
    }

    public static bool SetText(string text, bool excludeFromHistory = false) =>
        Write(excludeFromHistory, () => SetGlobal(CfUnicodeText, System.Text.Encoding.Unicode.GetBytes(text + "\0")));

    /// <summary>Image on the clipboard: PNG bytes (preferred, keeps transparency) or a DIB converted to BGRA.</summary>
    public static (byte[]? Png, PixelImage? Image) GetImage() =>
        Read<(byte[]? Png, PixelImage? Image)>(() =>
        {
            var png = ReadGlobal(RegisterClipboardFormat("PNG"));
            if (png is not null)
            {
                return (png, (PixelImage?)null);
            }

            var dib = ReadGlobal(CF_DIB);
            return (null, dib is null ? null : FromDib(dib));
        }, (null, null));

    /// <summary>Files on the clipboard (copied in Explorer).</summary>
    public static IReadOnlyList<string> GetFiles() =>
        Read<IReadOnlyList<string>>(() =>
        {
            var handle = GetClipboardData(CfHdrop);
            if (handle == IntPtr.Zero)
            {
                return [];
            }

            var count = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
            var files = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(handle, i, null, 0);
                var buffer = new char[length + 1];
                DragQueryFile(handle, i, buffer, (uint)buffer.Length);
                files.Add(new string(buffer, 0, (int)length));
            }

            return files;
        }, []);

    private static bool Write(bool excludeFromHistory, Func<bool> setData)
    {
        var owner = CreateWindowEx(0, "STATIC", "SnapNova.Clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            // The clipboard can be locked by another app for a moment (clipboard managers, RDP).
            var opened = false;
            for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(owner)); attempt++)
            {
                Thread.Sleep(30);
            }

            if (!opened)
            {
                return false;
            }

            try
            {
                EmptyClipboard();
                var ok = setData();
                if (excludeFromHistory)
                {
                    // Documented formats: no clipboard history, no cloud sync, ignored by clipboard monitors.
                    SetGlobal(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), [0]);
                    SetGlobal(RegisterClipboardFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0));
                    SetGlobal(RegisterClipboardFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(0));
                }

                return ok;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (owner != IntPtr.Zero)
            {
                DestroyWindow(owner);
            }
        }
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        var opened = false;
        for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(IntPtr.Zero)); attempt++)
        {
            Thread.Sleep(30);
        }

        if (!opened)
        {
            return fallback;
        }

        try
        {
            return read();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? ReadGlobal(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (long)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero || size <= 0 || size > 512L * 1024 * 1024)
        {
            return null;
        }

        try
        {
            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>CF_DIB (24/32 bit, BI_RGB or BI_BITFIELDS, bottom-up or top-down) → opaque BGRA.</summary>
    internal static PixelImage? FromDib(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var rawHeight = BitConverter.ToInt32(dib, 8);
        var bits = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        if (width <= 0 || rawHeight == 0 || bits is not (24 or 32) || compression is not (0 or 3))
        {
            return null;
        }

        var height = Math.Abs(rawHeight);
        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0) + colorsUsed * 4;
        var sourceStride = ((width * bits + 31) / 32) * 4;
        if (offset + (long)sourceStride * height > dib.Length)
        {
            return null;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = offset + (rawHeight > 0 ? height - 1 - y : y) * sourceStride;
            for (var x = 0; x < width; x++)
            {
                var s = sourceRow + x * (bits / 8);
                var t = y * stride + x * 4;
                pixels[t] = dib[s];
                pixels[t + 1] = dib[s + 1];
                pixels[t + 2] = dib[s + 2];
                pixels[t + 3] = 255;
            }
        }

        return new PixelImage(width, height, pixels, stride);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr mem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr drop, uint index, char[]? file, uint length);

    /// <summary>BITMAPINFOHEADER + bottom-up 32-bit rows (the classic DIB layout).</summary>
    private static byte[] Dib(PixelImage image)
    {
        var headerSize = Marshal.SizeOf<BITMAPINFOHEADER>();
        var rowBytes = image.Width * 4;
        var data = new byte[headerSize + rowBytes * image.Height];
        var header = new BITMAPINFOHEADER
        {
            biSize = headerSize,
            biWidth = image.Width,
            biHeight = image.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = rowBytes * image.Height
        };
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(header, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }

        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, y * image.Stride, data, headerSize + (image.Height - 1 - y) * rowBytes, rowBytes);
        }

        return data;
    }

    private static bool SetGlobal(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(memory);

        // On success the clipboard owns the memory; on failure it is still ours.
        if (SetClipboardData(format, memory) == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        return true;
    }
}
