namespace SnapNova.App.ViewModels;

/// <summary>
/// A list row that was just inserted: the view plays a short entrance animation once (Controls.Motion.Enter).
/// </summary>
public interface IAnimatedRow
{
    /// <summary>Environment.TickCount64 when the row was inserted; 0 = no animation.</summary>
    long EnterTicks { get; set; }

    /// <summary>Stagger delay in milliseconds.</summary>
    int EnterDelay { get; set; }
}
