using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SnapNova.Platform.Windows.Recording;

/// <summary>
/// Sound of ONE program (and its child processes) – Windows "process loopback" (Windows 10 2004 / build 19041 and later;
/// officially documented from build 20348). Used for additional recordings: each recording can take the sound of the
/// program in its area, even when two videos play at the same time. Two windows of the same program (e.g. two browser
/// windows) share one sound and cannot be separated.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const int AudclntSharemodeShared = 0;
    private const uint AudclntStreamflagsLoopback = 0x00020000;
    private const uint AudclntStreamflagsEventcallback = 0x00040000;
    private const uint AudclntStreamflagsAutoconvertpcm = 0x80000000;
    private const uint AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    private const uint AudclntBufferflagsSilent = 0x2;
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly uint _processId;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _thread;
    private volatile bool _running;
    private readonly AutoResetEvent _ready = new(false);

    public ProcessLoopbackCapture(int processId)
    {
        _processId = (uint)processId;
        WaveFormat = new WaveFormat(48000, 16, 2);
    }

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>Activates the audio client (asynchronous in Windows; waited for here, max. 5 s). Throws on failure.</summary>
    public void StartRecording()
    {
        _client = Activate(_processId);
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = (ushort)WaveFormat.Channels,
            SamplesPerSec = (uint)WaveFormat.SampleRate,
            BitsPerSample = (ushort)WaveFormat.BitsPerSample,
            BlockAlign = (ushort)WaveFormat.BlockAlign,
            AvgBytesPerSec = (uint)WaveFormat.AverageBytesPerSecond,
            Size = 0
        };
        Check(_client.Initialize(AudclntSharemodeShared,
            AudclntStreamflagsLoopback | AudclntStreamflagsEventcallback | AudclntStreamflagsAutoconvertpcm | AudclntStreamflagsSrcDefaultQuality,
            2_000_000, 0, ref format, IntPtr.Zero));
        Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
        var iid = IidAudioCaptureClient;
        Check(_client.GetService(ref iid, out var service));
        _capture = (IAudioCaptureClient)service;
        Check(_client.Start());

        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "SnapNova process audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void StopRecording()
    {
        _running = false;
        _ready.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Pump()
    {
        Exception? error = null;
        var blockAlign = WaveFormat.BlockAlign;
        var buffer = new byte[WaveFormat.AverageBytesPerSecond];
        try
        {
            while (_running)
            {
                _ready.WaitOne(100);
                while (_running && _capture!.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    if (_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0)
                    {
                        break;
                    }

                    var bytes = (int)frames * blockAlign;
                    if (buffer.Length < bytes)
                    {
                        buffer = new byte[bytes];
                    }

                    if ((flags & AudclntBufferflagsSilent) != 0)
                    {
                        Array.Clear(buffer, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    _capture.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            error = e;
        }
        finally
        {
            _client?.Stop();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    public void Dispose()
    {
        StopRecording();
        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }

        if (_client is not null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }

        _ready.Dispose();
    }

    // ---------------------------------------------------------------- activation

    private static IAudioClient Activate(uint processId)
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = processId,
            ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(parameters, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                Vt = 65, // VT_BLOB
                Size = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                Data = paramsPtr
            }, variantPtr, false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, IidAudioClient, variantPtr, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new COMException("Process loopback activation timed out.");
            }

            handler.Operation!.GetActivateResult(out var hr, out var unknown);
            Check(hr);
            return (IAudioClient)unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT holding a BLOB (x64 layout: vt + 3 reserved words, cbSize, pointer at offset 16).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint frames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint frames);

        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>Marks the handler as free-threaded (Windows calls it on any thread).</summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject
    {
    }

    [ComVisible(true)]
    internal sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new(false);

        public IActivateAudioInterfaceAsyncOperation? Operation { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Operation = activateOperation;
            Done.Set();
        }
    }
}
