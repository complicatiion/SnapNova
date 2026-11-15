using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SnapNova.Core.Capture;

namespace SnapNova.Platform.Windows.Automation;

/// <summary>
/// Element snapping (hold Ctrl in the overlay): finds the smallest UI element (toolbar, panel, button, list) of a window
/// at a screen point with UI Automation. The overlay covers the screen, so instead of ElementFromPoint the element tree of
/// the window below is walked (control view). Runs on its own MTA thread, newest request wins.
/// Plain COM vtable calls, no interop assembly.
/// </summary>
public sealed unsafe class ElementFinder : IDisposable
{
    private static readonly Guid ClsidCuiAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IidIuiAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    // IUIAutomation: 6 ElementFromHandle, 14 get_ControlViewWalker
    private const int SlotElementFromHandle = 6;
    private const int SlotControlViewWalker = 14;

    // IUIAutomationTreeWalker: 4 GetFirstChildElement, 6 GetNextSiblingElement
    private const int SlotFirstChild = 4;
    private const int SlotNextSibling = 6;

    // IUIAutomationElement: 43 get_CurrentBoundingRectangle
    private const int SlotBoundingRectangle = 43;
    private const int SlotRelease = 2;
    private const int MaxCalls = 1500;

    private readonly BlockingCollection<Request> _requests = new(new ConcurrentStack<Request>());
    private readonly Thread _thread;

    public ElementFinder()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SnapNova.ElementFinder" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private sealed record Request(IntPtr Window, int X, int Y, TaskCompletionSource<ScreenRect?> Done);

    /// <summary>Smallest element containing the point (virtual-desktop pixels), or null.</summary>
    public Task<ScreenRect?> FindAsync(IntPtr window, int x, int y)
    {
        var done = new TaskCompletionSource<ScreenRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.IsAddingCompleted)
        {
            _requests.Add(new Request(window, x, y, done));
        }
        else
        {
            done.TrySetResult(null);
        }

        return done.Task;
    }

    private void Loop()
    {
        IntPtr automation = IntPtr.Zero, walker = IntPtr.Zero;
        try
        {
            var clsid = ClsidCuiAutomation;
            var iid = IidIuiAutomation;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out automation) < 0 || automation == IntPtr.Zero)
            {
                automation = IntPtr.Zero;
            }
            else
            {
                IntPtr w;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(automation, SlotControlViewWalker))(automation, &w) >= 0)
                {
                    walker = w;
                }
            }

            foreach (var request in _requests.GetConsumingEnumerable())
            {
                // Only the newest request counts (the mouse has moved on).
                var latest = request;
                while (_requests.TryTake(out var newer))
                {
                    latest.Done.TrySetResult(null);
                    latest = newer;
                }

                ScreenRect? result = null;
                if (automation != IntPtr.Zero && walker != IntPtr.Zero)
                {
                    try
                    {
                        result = Find(automation, walker, latest.Window, latest.X, latest.Y);
                    }
                    catch (Exception e) when (e is COMException or InvalidOperationException)
                    {
                    }
                }

                latest.Done.TrySetResult(result);
            }
        }
        finally
        {
            Release(walker);
            Release(automation);
        }
    }

    private static ScreenRect? Find(IntPtr automation, IntPtr walker, IntPtr window, int x, int y)
    {
        IntPtr root;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(automation, SlotElementFromHandle))(automation, window, &root) < 0
            || root == IntPtr.Zero)
        {
            return null;
        }

        var calls = 0;
        var current = root;
        var best = Bounds(current);
        try
        {
            for (var depth = 0; depth < 30 && calls < MaxCalls; depth++)
            {
                IntPtr child;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(walker, SlotFirstChild))(walker, current, &child) < 0)
                {
                    break;
                }

                IntPtr chosen = IntPtr.Zero;
                ScreenRect? chosenRect = null;
                while (child != IntPtr.Zero && calls++ < MaxCalls)
                {
                    var rect = Bounds(child);
                    if (rect is { IsEmpty: false } r && r.Contains(x, y) && (chosenRect is null || (long)r.Width * r.Height < (long)chosenRect.Value.Width * chosenRect.Value.Height))
                    {
                        Release(chosen);
                        chosen = child;
                        chosenRect = r;
                    }

                    IntPtr next;
                    var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(walker, SlotNextSibling))(walker, child, &next);
                    if (child != chosen)
                    {
                        Release(child);
                    }

                    child = hr >= 0 ? next : IntPtr.Zero;
                }

                if (chosen == IntPtr.Zero)
                {
                    break;
                }

                if (current != root)
                {
                    Release(current);
                }

                current = chosen;
                best = chosenRect;
            }
        }
        finally
        {
            if (current != root)
            {
                Release(current);
            }

            Release(root);
        }

        return best;
    }

    private static ScreenRect? Bounds(IntPtr element)
    {
        Native.RECT rect;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Native.RECT*, int>)Slot(element, SlotBoundingRectangle))(element, &rect) < 0)
        {
            return null;
        }

        var r = new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return r.Width >= 4 && r.Height >= 4 ? r : null;
    }

    private static IntPtr Slot(IntPtr comObject, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    private static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, SlotRelease))(comObject);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr instance);

    public void Dispose() => _requests.CompleteAdding();
}
