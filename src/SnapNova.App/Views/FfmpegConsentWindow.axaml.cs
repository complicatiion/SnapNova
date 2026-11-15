using Avalonia.Controls;
using SnapNova.App.ViewModels;

namespace SnapNova.App.Views;

/// <summary>FFmpeg license notice and opt-in download (shown before the first video capture).</summary>
public partial class FfmpegConsentWindow : Window
{
    public FfmpegConsentWindow()
    {
        InitializeComponent();
        Services.WindowChrome.Apply(this, maximize: false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FfmpegConsentViewModel vm)
            {
                vm.CloseRequested += ok =>
                {
                    Result = ok;
                    Close(ok);
                };
            }
        };

        // Closing with the window button during a download cancels it.
        Closing += (_, _) =>
        {
            if (DataContext is FfmpegConsentViewModel { IsWorking: true } vm)
            {
                vm.CancelCommand.Execute(null);
            }
        };
    }

    /// <summary>Result for the non-modal case (tray start without an open window).</summary>
    public bool Result { get; private set; }
}
