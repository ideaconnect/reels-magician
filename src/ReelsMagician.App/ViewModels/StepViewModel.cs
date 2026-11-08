using ReelsMagician.App.Localization;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// One chip of the step strip: the step's number, its name, and how far the project has got
/// with it. The chip's look follows <see cref="State"/>: a check once done, the accent ring on
/// the step to do next, and dimmed while an earlier step is still missing.
/// </summary>
/// <param name="titleKey">The resource key of the step's name, such as <c>Step_Setup</c>.</param>
public sealed class StepViewModel(int number, string titleKey) : ViewModelBase
{
    public int Number { get; } = number;

    public string Title => Localizer.Instance[titleKey];

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
    public string AccessibleName => Localizer.Instance.Format(State switch
    {
        StepState.Done => nameof(Strings.Step_AccessibleName_Done),
        StepState.Current => nameof(Strings.Step_AccessibleName_Current),
        _ => nameof(Strings.Step_AccessibleName_Upcoming),
    }, Number, ReelSteps.Count, Title);

    /// <summary>After a switch of language: the name and what a screen reader says.</summary>
    public void RefreshTexts()
    {
        Raise(nameof(Title));
        Raise(nameof(AccessibleName));
    }
}
