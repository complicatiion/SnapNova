using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SnapNova.Platform.Windows.Recording;

/// <summary>
/// System sound (WASAPI loopback of the default speakers) and microphone, each into its own WAV file.
/// Writing starts paused: the video recorder switches it on when FFmpeg delivers its first frame and off during pauses,
/// so audio and the joined video segments keep the same length.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly List<Track> _tracks = [];
    private volatile bool _paused = true;

    /// <param name="processId">Above 0: sound of that program only (process loopback) instead of the whole system sound.</param>
    public AudioRecorder(string folder, bool systemSound, bool microphone, int processId = 0)
    {
        using var devices = new MMDeviceEnumerator();
        if (processId > 0)
        {
            _tracks.Add(new Track(Path.Combine(folder, "application.wav"), new ProcessLoopbackCapture(processId), null, this));
            systemSound = false;
        }

        if (systemSound && TryGetDefault(devices, DataFlow.Render) is { } speakers)
        {
            var loopback = new WasapiLoopbackCapture(speakers);

            // Loopback only delivers data while something plays. A silent stream on the same device keeps it running,
            // otherwise quiet passages would be missing and the sound would drift ahead of the picture.
            var keepAlive = new WasapiOut(speakers, AudioClientShareMode.Shared, false, 100);
            keepAlive.Init(new SilenceProvider(loopback.WaveFormat));
            _tracks.Add(new Track(Path.Combine(folder, "system.wav"), loopback, keepAlive, this));
        }

        if (microphone && TryGetDefault(devices, DataFlow.Capture) is { } mic)
        {
            _tracks.Add(new Track(Path.Combine(folder, "microphone.wav"), new WasapiCapture(mic), null, this));
        }
    }

    /// <summary>WAV files with data (after <see cref="Stop"/>).</summary>
    public IReadOnlyList<string> Files => _tracks.Where(t => t.HasData).Select(t => t.File).ToList();

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Microphone or speakers missing/in use – the recording continues without that track.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public void Start()
    {
        var warnings = new List<string>();
        foreach (var track in _tracks)
        {
            try
            {
                track.Start();
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                           or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
            {
                warnings.Add($"{Path.GetFileNameWithoutExtension(track.File)}: {e.Message}");
            }
        }

        Warnings = warnings;
    }

    public void Stop()
    {
        _paused = true;
        foreach (var track in _tracks)
        {
            track.Stop();
        }
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            track.Dispose();
        }
    }

    private static MMDevice? TryGetDefault(MMDeviceEnumerator devices, DataFlow flow)
    {
        try
        {
            return devices.HasDefaultAudioEndpoint(flow, Role.Multimedia) ? devices.GetDefaultAudioEndpoint(flow, Role.Multimedia) : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private sealed class Track : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly WasapiOut? _keepAlive;
        private readonly AudioRecorder _owner;
        private readonly ManualResetEventSlim _stopped = new(true);
        private readonly object _gate = new();
        private WaveFileWriter? _writer;

        public Track(string file, IWaveIn capture, WasapiOut? keepAlive, AudioRecorder owner)
        {
            File = file;
            _capture = capture;
            _keepAlive = keepAlive;
            _owner = owner;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => _stopped.Set();
        }

        public string File { get; }

        public bool HasData { get; private set; }

        public void Start()
        {
            _writer = new WaveFileWriter(File, _capture.WaveFormat);
            _keepAlive?.Play();
            _stopped.Reset();
            _capture.StartRecording();
        }

        public void Stop()
        {
            try
            {
                _capture.StopRecording();
                _stopped.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }

            _keepAlive?.Stop();
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (_owner._paused || e.BytesRecorded == 0)
            {
                return;
            }

            lock (_gate)
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                HasData = true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }

            _capture.Dispose();
            _keepAlive?.Dispose();
            _stopped.Dispose();
        }
    }
}
