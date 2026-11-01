using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.App.ViewModels;

/// <summary>One tickable category in the "free up space" list.</summary>
public sealed class CleanupRowViewModel(DataFolderItem item, Action onChanged) : ViewModelBase
{
    public string Key => item.Key;
    public string Name => item.Name;
    public string Detail => item.Detail;
    public string SizeText => item.SizeText;
    public long Bytes => item.Bytes;

    /// <summary>Nothing to remove, so the row is shown but cannot be selected.</summary>
    public bool CanSelect => item.Exists && item.Bytes >= 0;

    private bool _isSelected = item.SelectedByDefault && item.Exists;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value && CanSelect)) return;
            onChanged();
        }
    }
}
