using ReelsMagician.Core.Projects;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// One chip of the step strip: the step's number, its name, and how far the project has got
/// with it. The chip's look follows <see cref="State"/>: a check once done, the accent ring on
/// the step to do next, and dimmed while an earlier step is still missing.
/// </summary>
public sealed class StepViewModel(int number, string title) : ViewModelBase
{
    public int Number { get; } = number;

    public string Title { get; } = title;

    private StepState _state;
    public StepState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsDone));
            Raise(nameof(IsCurrent));
            Raise(nameof(IsUpcoming));
            Raise(nameof(AccessibleName));
        }
    }

    public bool IsDone => State == StepState.Done;
    public bool IsCurrent => State == StepState.Current;
    public bool IsUpcoming => State == StepState.Upcoming;

    /// <summary>What a screen reader says for the chip, such as "Step 3 of 6, Subtitles, done".</summary>
    public string AccessibleName => $"Step {Number} of {ReelSteps.Count}, {Title}, " + State switch
    {
        StepState.Done => "done",
        StepState.Current => "next",
        _ => "not started",
    };
}
