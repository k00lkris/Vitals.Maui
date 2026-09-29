using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingPatientSetupViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly AuthService _auth;

    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    public bool IsSelf { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;

    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private DateTime _dob = DateTime.Today.AddYears(-40);
    [ObservableProperty] private string _gender = string.Empty;
    [ObservableProperty] private string _heightFeet = string.Empty;
    [ObservableProperty] private string _heightInches = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingPatientSetupViewModel(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
    }

    /// <summary>
    /// Configures this screen for either the account holder or a family
    /// member. For "myself," the name is pre-filled from the Google
    /// account's display name (still editable) since we already know it —
    /// no reason to ask a question we already have the answer to.
    /// </summary>
    public void Initialize(bool isSelf)
    {
        IsSelf = isSelf;

        if (isSelf)
        {
            Title = "Tell us about you";
            Subtitle = "This helps Vitals tailor what's normal for your readings.";

            var displayName = _auth.DisplayName ?? string.Empty;
            var parts = displayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            FirstName = parts.Length > 0 ? parts[0] : string.Empty;
            LastName = parts.Length > 1 ? parts[1] : string.Empty;
        }
        else
        {
            Title = "Who are you caring for?";
            Subtitle = "Add the person you'll be tracking readings and medications for.";
            FirstName = string.Empty;
            LastName = string.Empty;
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName))
        {
            StatusMessage = "First and last name are required.";
            return;
        }

        int? heightInches = null;
        if (!TryGetHeightInches(out heightInches, out var heightError))
        {
            StatusMessage = heightError;
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var payload = new
            {
                first_name = FirstName.Trim(),
                last_name = LastName.Trim(),
                dob = Dob.ToString("yyyy-MM-dd"),
                gender = string.IsNullOrWhiteSpace(Gender) ? null : Gender,
                height_inches = heightInches,
                relationship = IsSelf ? "self" : "caregiver",
            };

            var created = await _api.AddPatientAsync(payload);
            if (created is not null)
            {
                OnContinue?.Invoke();
            }
            else
            {
                StatusMessage = "Something went wrong saving this. Please try again.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryGetHeightInches(out int? totalInches, out string error)
    {
        totalInches = null;
        error = string.Empty;

        var feetText = HeightFeet?.Trim() ?? string.Empty;
        var inchesText = HeightInches?.Trim() ?? string.Empty;

        // Height is optional during onboarding because a caregiver may not
        // know it yet. Settings provides a persistent place to add/update it.
        if (string.IsNullOrEmpty(feetText) && string.IsNullOrEmpty(inchesText))
            return true;

        if (!int.TryParse(feetText, out var feet) || feet < 1 || feet > 8)
        {
            error = "Height feet must be between 1 and 8.";
            return false;
        }

        var inches = 0;
        if (!string.IsNullOrEmpty(inchesText) &&
            (!int.TryParse(inchesText, out inches) || inches < 0 || inches > 11))
        {
            error = "Height inches must be between 0 and 11.";
            return false;
        }

        totalInches = feet * 12 + inches;
        return true;
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
