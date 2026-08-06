using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingPlanSelectionViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly AuthService _auth;

    // Wired by the page's code-behind.
    public Action? OnBack { get; set; }
    public Action? OnIndividualOrFreeSelected { get; set; }  // -> Personalization
    public Action? OnFamilySelected { get; set; }            // -> Patient Setup (skip Personalization)
    public Action? OnJoinSelected { get; set; }               // -> enter invite code screen

    private static readonly Color SelectedColor = Color.FromArgb("#1976d2");
    private static readonly Color UnselectedColor = Color.FromArgb("#2a3a5c");

    // "individual" | "family" | "free" | "" (none chosen yet) — same
    // select-then-confirm pattern as OnboardingPersonalizationViewModel,
    // rather than navigating away the instant a card is tapped. Tapping
    // between options to compare before deciding shouldn't accidentally
    // create a household.
    [ObservableProperty] private string _selectedTier = string.Empty;

    [ObservableProperty] private Color _individualCardColor = UnselectedColor;
    [ObservableProperty] private Color _familyCardColor = UnselectedColor;
    [ObservableProperty] private Color _freeCardColor = UnselectedColor;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingPlanSelectionViewModel(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
    }

    [RelayCommand]
    public void SelectIndividual()
    {
        SelectedTier = "individual";
    }

    [RelayCommand]
    public void SelectFamily()
    {
        SelectedTier = "family";
    }

    [RelayCommand]
    public void SelectFree()
    {
        SelectedTier = "free";
    }

    partial void OnSelectedTierChanged(string value)
    {
        IndividualCardColor = value == "individual" ? SelectedColor : UnselectedColor;
        FamilyCardColor = value == "family" ? SelectedColor : UnselectedColor;
        FreeCardColor = value == "free" ? SelectedColor : UnselectedColor;
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// Confirms the highlighted card — this is what actually creates the
    /// household (intent only, no billing yet, that's Phase 7) and routes
    /// accordingly. Family skips Personalization entirely, since choosing
    /// it already answers "who are you tracking for" (more than one
    /// person) — Individual and Free still need that question asked.
    /// </summary>
    [RelayCommand]
    public async Task ContinueAsync()
    {
        if (string.IsNullOrEmpty(SelectedTier))
        {
            StatusMessage = "Choose a plan to continue.";
            return;
        }

        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _api.SelectTierAsync(SelectedTier);
            if (result is null)
            {
                StatusMessage = "Something went wrong. Please try again.";
                return;
            }

            await _auth.UpdateSessionAsync(result.Token, result.HouseholdId);

            if (SelectedTier == "family")
            {
                OnFamilySelected?.Invoke();
            }
            else
            {
                OnIndividualOrFreeSelected?.Invoke();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Joining isn't a tier choice to highlight-then-confirm — it's a
    // distinct path (an existing household, not a new one), so it stays
    // an immediate action like it already was.
    [RelayCommand]
    public void JoinExistingHousehold()
    {
        OnJoinSelected?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
