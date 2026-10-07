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
    public Action? OnStandardOrTrialSelected { get; set; }   // -> Personalization
    public Action? OnFamilySelected { get; set; }            // -> Patient Setup (skip Personalization)
    public Action? OnJoinSelected { get; set; }               // -> enter invite code screen

    private static readonly Color SelectedColor = Color.FromArgb("#1976d2");
    private static readonly Color UnselectedColor = Color.FromArgb("#b2dff2");
    private static readonly Color SelectedFillColor = Color.FromArgb("#661976D2");
    private static readonly Color UnselectedFillColor = Color.FromArgb("#f0f9ff");

    // "standard" | "family" | "trial" | "" (none chosen yet) — same
    // select-then-confirm pattern as OnboardingPersonalizationViewModel,
    // rather than navigating away the instant a card is tapped. Tapping
    // between options to compare before deciding shouldn't accidentally
    // create a household.
    [ObservableProperty] private string _selectedTier = string.Empty;

    [ObservableProperty] private Color _standardCardColor = UnselectedColor;
    [ObservableProperty] private Color _familyCardColor = UnselectedColor;
    [ObservableProperty] private Color _trialCardColor = UnselectedColor;

    [ObservableProperty] private Color _standardCardBackground = UnselectedFillColor;
    [ObservableProperty] private Color _familyCardBackground = UnselectedFillColor;
    [ObservableProperty] private Color _trialCardBackground = UnselectedFillColor;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingPlanSelectionViewModel(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
    }

    [RelayCommand]
    public void SelectStandard()
    {
        SelectedTier = "standard";
    }

    [RelayCommand]
    public void SelectFamily()
    {
        SelectedTier = "family";
    }

    [RelayCommand]
    public void SelectTrial()
    {
        SelectedTier = "trial";
    }

    partial void OnSelectedTierChanged(string value)
    {
        StandardCardColor = value == "standard" ? SelectedColor : UnselectedColor;
        FamilyCardColor = value == "family" ? SelectedColor : UnselectedColor;
        TrialCardColor = value == "trial" ? SelectedColor : UnselectedColor;

        StandardCardBackground = value == "standard" ? SelectedFillColor : UnselectedFillColor;
        FamilyCardBackground = value == "family" ? SelectedFillColor : UnselectedFillColor;
        TrialCardBackground = value == "trial" ? SelectedFillColor : UnselectedFillColor;

        StatusMessage = string.Empty;
    }

    /// <summary>
    /// Confirms the highlighted card — this is what actually creates the
    /// household (intent only, no billing yet, that's Phase 7) and routes
    /// accordingly. Family skips Personalization entirely, since choosing
    /// it already answers "who are you tracking for" (more than one
    /// person) — Standard and Decide Later still need that question asked.
    /// </summary>
    [RelayCommand]
    public async Task ContinueAsync()
    {
        if (string.IsNullOrEmpty(SelectedTier))
        {
            StatusMessage = "Choose a plan or select Decide Later to continue.";
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
                OnStandardOrTrialSelected?.Invoke();
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
