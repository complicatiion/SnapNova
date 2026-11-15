using System.Collections.Concurrent;
using SnapNova.Core.Capture;
using static SnapNova.Platform.Windows.Native;

namespace SnapNova.Platform.Windows.Input;

/// <summary>
/// System-wide hotkeys with RegisterHotKey on a dedicated thread (no window needed: with hwnd = NULL, WM_HOTKEY is posted
/// to the registering thread's message queue). Registration changes are marshalled to that thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const uint WmRun = WM_APP + 1;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly HashSet<int> _registered = [];
    private readonly Thread _thread;
    private uint _threadId;

    public HotkeyService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SnapNova.Hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Raised on the hotkey thread with the id passed to <see cref="SetAsync"/>.</summary>
    public event Action<int>? Pressed;

    /// <summary>Replaces all hotkeys. Result: id → registered (false = taken by another app or Windows).</summary>
    public Task<IReadOnlyDictionary<int, bool>> SetAsync(IEnumerable<(int Id, Hotkey Key)> hotkeys)
    {
        var list = hotkeys.ToList();
        var done = new TaskCompletionSource<IReadOnlyDictionary<int, bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            foreach (var id in _registered)
            {
                UnregisterHotKey(IntPtr.Zero, id);
            }

            _registered.Clear();
            var result = new Dictionary<int, bool>();
            foreach (var (id, key) in list)
            {
                if (key.IsEmpty)
                {
                    continue;
                }

                var ok = RegisterHotKey(IntPtr.Zero, id, key.Modifiers, key.VirtualKey);
                if (ok)
                {
                    _registered.Add(id);
                }

                result[id] = ok;
            }

            done.TrySetResult(result);
        });
        return done.Task;
    }

    private void Run(Action action)
    {
        _work.Enqueue(action);
        PostThreadMessage(_threadId, WmRun, IntPtr.Zero, IntPtr.Zero);
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();

        // Creates the message queue of this thread before anyone posts to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                try
                {
                    Pressed?.Invoke((int)msg.wParam);
                }
                catch (Exception)
                {
                    // A failing handler must not end the hotkey thread.
                }
            }
            else if (msg.message == WmRun)
            {
                while (_work.TryDequeue(out var action))
                {
                    action();
                }
            }
        }

        foreach (var id in _registered)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
