using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingPersonalizationViewModel : ObservableObject
{
    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    private static readonly Color SelectedColor = Color.FromArgb("#1976d2");
    private static readonly Color UnselectedColor = Color.FromArgb("#0f3460");

    // "myself" | "family" | "both" | "" (none chosen yet)
    [ObservableProperty] private string _selection = string.Empty;

    [ObservableProperty] private Color _myselfCardColor = UnselectedColor;
    [ObservableProperty] private Color _familyCardColor = UnselectedColor;
    [ObservableProperty] private Color _bothCardColor = UnselectedColor;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingPersonalizationViewModel()
    {
        // Restore a prior in-progress selection if onboarding was interrupted
        // (e.g. app backgrounded) rather than defaulting to nothing chosen.
        Selection = Preferences.Get("onboarding_tracking_for", string.Empty);
        UpdateCardColors();
    }

    [RelayCommand]
    public void SelectMyself()
    {
        Selection = "myself";
    }

    [RelayCommand]
    public void SelectFamily()
    {
        Selection = "family";
    }

    [RelayCommand]
    public void SelectBoth()
    {
        Selection = "both";
    }

    partial void OnSelectionChanged(string value)
    {
        UpdateCardColors();
        Preferences.Set("onboarding_tracking_for", value);
        StatusMessage = string.Empty;
    }

    private void UpdateCardColors()
    {
        MyselfCardColor = Selection == "myself" ? SelectedColor : UnselectedColor;
        FamilyCardColor = Selection == "family" ? SelectedColor : UnselectedColor;
        BothCardColor = Selection == "both" ? SelectedColor : UnselectedColor;
    }

    [RelayCommand]
    public void Continue()
    {
        if (string.IsNullOrEmpty(Selection))
        {
            StatusMessage = "Choose one to continue.";
            return;
        }

        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
