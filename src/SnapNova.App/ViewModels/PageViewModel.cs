using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SnapNova.App.Localization;

namespace SnapNova.App.ViewModels;

/// <summary>A page of the main window (sidebar entry).</summary>
public abstract partial class PageViewModel : ObservableObject
{
    private readonly string _titleKey;

    protected PageViewModel(string titleKey, string iconKey)
    {
        _titleKey = titleKey;
        Icon = Res.Icon(iconKey);
        Loc.LanguageChanged += () => OnPropertyChanged(nameof(Title));
    }

    public string Title => Loc.T(_titleKey);

    public Geometry? Icon { get; }

    /// <summary>Sidebar style binds this (shared with CLEANit's navigation style).</summary>
    public bool IsHidden => false;

    /// <summary>Called each time the page is shown.</summary>
    public virtual void OnShown()
    {
    }
}
