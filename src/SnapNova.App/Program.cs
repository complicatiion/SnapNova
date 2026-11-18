using System.IO.Pipes;
using System.Text;
using Avalonia;
using SnapNova.Core.Capture;
using SnapNova.Platform.Windows.Startup;

namespace SnapNova.App;

/// <summary>
/// One executable, one instance per Windows session. A second start (double-click on a .snova file, "--capture=region"
/// from a shortcut/Stream Deck, the Start menu entry) hands its arguments to the running instance over a named pipe.
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\SnapNova.App";

    /// <summary>Start of the new instance after "Reset SnapNova".</summary>
    public const string RestartArgument = "--after-reset";

    /// <summary>Pipe name per user session (Local\ is not available for pipes).</summary>
    private static string PipeName => $"SnapNova.App.{Environment.UserName}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    internal static StartRequest Initial { get; private set; } = new(false, null, null);

    /// <summary>Raised on a background thread when a second start forwarded its arguments.</summary>
    internal static event Action<StartRequest>? Forwarded;

    /// <summary>Setting of this user; before the first start the setup defaults apply.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool UserWantsAutostart()
    {
        var settings = Services.UiSettings.Load();
        return settings.SetupDefaultsApplied
            ? settings.StartWithWindows
            : (SetupDefaults.Read() ?? SetupDefaults.Fallback).StartWithWindows || Autostart.IsMachineWide;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        // Installer commands (no window, no single-instance check).
        if (OperatingSystem.IsWindows())
        {
            var exe = Environment.ProcessPath ?? "";
            if (Has("--apply-setup-defaults"))
            {
                Services.FirstRun.ApplySetupDefaults(exe, force: true);
                return 0;
            }

            if (Has("--unregister-autostart"))
            {
                Autostart.Disable();
                return 0;
            }
        }

        // Machine-wide autostart (terminal server): every user's own switch decides whether SnapNova stays running.
        if (OperatingSystem.IsWindows() && Has(Autostart.MachineArgument) && !UserWantsAutostart())
        {
            return 0;
        }

        var request = StartRequest.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        // Restart after "Reset SnapNova": wait until the old instance has ended instead of forwarding to it.
        if (!createdNew && Has(RestartArgument))
        {
            try
            {
                createdNew = mutex.WaitOne(TimeSpan.FromSeconds(20));
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            // Already running: forward and exit. No arguments = "show the gallery".
            return Forward(request) ? 0 : 1;
        }

        Initial = request;
        using var server = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(server.Token));

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            server.Cancel();
            mutex.ReleaseMutex();
        }
    }

    private static bool Forward(StartRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(request.Serialize());
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly: other users/sessions cannot send commands to this instance.
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(ct);
                if (text.Length < 8192)
                {
                    Forwarded?.Invoke(StartRequest.Deserialize(text));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away mid-message – wait for the next one.
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <param name="Tray">--tray: start without window (autostart).</param>
/// <param name="Capture">--capture=region|window|fullscreen|scrolling|video|video-fullscreen</param>
/// <param name="File">.snova / image / video to open in the editor or player.</param>
internal sealed record StartRequest(bool Tray, CaptureMode? Capture, string? File)
{
    public static StartRequest Parse(string[] args)
    {
        var tray = false;
        CaptureMode? capture = null;
        string? file = null;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
            }
            else if (arg.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase))
            {
                capture = CaptureModeExtensions.Parse(arg["--capture=".Length..]);
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && System.IO.File.Exists(arg))
            {
                file = Path.GetFullPath(arg);
            }
        }

        return new StartRequest(tray, capture, file);
    }

    public string Serialize() => $"{(Tray ? 1 : 0)}\n{Capture?.ToString() ?? ""}\n{File ?? ""}";

    public static StartRequest Deserialize(string text)
    {
        var lines = text.Split('\n');
        var tray = lines.Length > 0 && lines[0] == "1";
        CaptureMode? capture = lines.Length > 1 && Enum.TryParse<CaptureMode>(lines[1], out var mode) ? mode : null;
        var file = lines.Length > 2 && lines[2].Length > 0 && System.IO.File.Exists(lines[2]) ? lines[2] : null;
        return new StartRequest(tray, capture, file);
    }
}
