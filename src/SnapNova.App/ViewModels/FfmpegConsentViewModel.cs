using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapNova.App.Localization;
using SnapNova.Core.Media;

namespace SnapNova.App.ViewModels;

/// <summary>License notice + download in one dialog: page 1 = notice, page 2 = progress / error.</summary>
public sealed partial class FfmpegConsentViewModel(FfmpegProvisioner provisioner, FfmpegVariant variant) : ObservableObject
{
    private CancellationTokenSource? _cts;

    /// <summary>Set by FfmpegGate: file picker for "Use existing ffmpeg.exe".</summary>
    public Func<Task<string?>>? PickExisting { get; set; }

    /// <summary>Closes the window with the result (true = FFmpeg ready).</summary>
    public event Action<bool>? CloseRequested;

    public string AssetName { get; } = FfmpegProvisioner.AssetName(variant);

    public bool IsGplVariant { get; } = variant == FfmpegVariant.Gpl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _noticeRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _isWorking;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _errorText;

    public bool CanAccept => NoticeRead && !IsWorking;

    [RelayCommand]
    private async Task AcceptAndDownloadAsync()
    {
        provisioner.AcceptNotice(variant);
        IsWorking = true;
        ErrorText = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<FfmpegProgress>(p =>
        {
            Progress = p.Fraction * 100;
            StatusText = p.Step switch
            {
                FfmpegStep.Checksums => Loc.T("FfmpegStepChecksums"),
                FfmpegStep.Downloading => Loc.F("FfmpegStepDownloading", ((p.BytesDone ?? 0) / 1048576.0).ToString("0")),
                FfmpegStep.Extracting => Loc.T("FfmpegStepExtracting"),
                FfmpegStep.Validating => Loc.T("FfmpegStepValidating"),
                _ => Loc.T("FfmpegStepDone")
            };
        });

        // One automatic retry: the floating "latest" release can change between checksum and download.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await provisioner.InstallAsync(progress, _cts.Token);
                CloseRequested?.Invoke(true);
                return;
            }
            catch (FfmpegIntegrityException) when (attempt == 1)
            {
            }
            catch (OperationCanceledException)
            {
                StatusText = Loc.T("FfmpegCancelled");
                break;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or FfmpegIntegrityException
                                          or UnauthorizedAccessException or System.IO.InvalidDataException)
            {
                // Typical in companies: proxy/firewall blocks github.com – then "Use existing ffmpeg.exe" is the way.
                ErrorText = Loc.F("FfmpegFailed", e.Message);
                break;
            }
        }

        IsWorking = false;
    }

    [RelayCommand]
    private async Task UseExistingAsync()
    {
        if (PickExisting is null || await PickExisting() is not { } path)
        {
            return;
        }

        provisioner.AcceptNotice(variant);
        try
        {
            await provisioner.UseExistingAsync(path, CancellationToken.None);
            CloseRequested?.Invoke(true);
        }
        catch (InvalidOperationException e)
        {
            ErrorText = e.Message;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsWorking)
        {
            _cts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(false);
    }
}
