using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserPreferencesService _preferences;
    private readonly AuthService _auth;
    private readonly PatientStateService _patientState;
    private readonly ApiService _api;
    private bool _suppressPreferenceSave;

    [ObservableProperty] string _currentTheme = "vitals_blue";
    [ObservableProperty] bool _showHeartRate = true;
    [ObservableProperty] bool _showSpo2 = true;
    [ObservableProperty] bool _showTemperature = true;
    [ObservableProperty] bool _showWeight = false;
    [ObservableProperty] bool _showGlucose = false;

    // Mutable demographics belong to the currently selected PATIENT, not
    // the signed-in account. This matters in caregiver households where one
    // user can manage several people.
    [ObservableProperty] string _profilePatientName = "No patient selected";
    [ObservableProperty] string _profileGender = string.Empty;
    [ObservableProperty] string _profileHeightFeet = string.Empty;
    [ObservableProperty] string _profileHeightInches = string.Empty;
    [ObservableProperty] bool _isSavingProfile;
    [ObservableProperty] string _profileStatusMessage = string.Empty;

    public bool HasSelectedPatient => _patientState.SelectedPatient is not null;

    public string DisplayName => _auth.DisplayName ?? "Unknown";
    public string Email => _auth.Email ?? "";

    // Maps the raw backend value ("password", "google.com", "apple.com")
    // to what's actually shown on screen — same computed pass-through
    // pattern as DisplayName/Email above, same reason it needs
    // RefreshAccountInfo() to update after an account switch.
    public string AuthProviderDisplay => _auth.AuthProvider switch
    {
        "password" => "Email",
        "google.com" => "Google",
        "apple.com" => "Apple",
        _ => "Unknown",
    };

    public string ThemeDarkColor => CurrentTheme == "dark" ? "#0f3460" : "Transparent";
    public string ThemeLightColor => CurrentTheme == "light" ? "#0f3460" : "Transparent";
    public string ThemeVitalsBlueColor => CurrentTheme == "vitals_blue" ? "#0f3460" : "Transparent";
    public string ThemeSystemColor => CurrentTheme == "system" ? "#0f3460" : "Transparent";

    public SettingsViewModel(
        UserPreferencesService preferences,
        AuthService auth,
        PatientStateService patientState,
        ApiService api)
    {
        _preferences = preferences;
        _auth = auth;
        _patientState = patientState;
        _api = api;
        LoadPreferences();
        LoadPatientProfile();
    }

    /// <summary>
    /// DisplayName/Email are computed pass-throughs to AuthService — the
    /// underlying value is always correct, but since this ViewModel is a
    /// Singleton (see MauiProgram.cs) and these aren't [ObservableProperty]
    /// fields, XAML bindings have no way to know they should re-read the
    /// value after an account switch. Nothing raises PropertyChanged for
    /// them on its own. Called explicitly from AppNavigation right after
    /// sign-in, alongside the same reload DashboardViewModel needs for the
    /// same underlying reason.
    /// </summary>
    public void RefreshAccountInfo()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(AuthProviderDisplay));
    }

    private void LoadPreferences()
    {
        ApplyPreferences(_preferences.LocalSnapshot());
    }

    public async Task LoadAsync()
    {
        var preferences = await _preferences.RefreshAsync();
        ApplyPreferences(preferences);
        LoadPatientProfile();
    }

    private void LoadPatientProfile()
    {
        var patient = _patientState.SelectedPatient;

        ProfileStatusMessage = string.Empty;
        ProfilePatientName = patient?.FullName ?? "No patient selected";
        ProfileGender = patient?.Gender ?? string.Empty;

        if (patient?.HeightInches is int totalInches)
        {
            ProfileHeightFeet = (totalInches / 12).ToString();
            ProfileHeightInches = (totalInches % 12).ToString();
        }
        else
        {
            ProfileHeightFeet = string.Empty;
            ProfileHeightInches = string.Empty;
        }

        OnPropertyChanged(nameof(HasSelectedPatient));
    }

    [RelayCommand]
    async Task SavePatientProfileAsync()
    {
        var patient = _patientState.SelectedPatient;
        if (patient is null)
        {
            ProfileStatusMessage = "Select a patient first.";
            return;
        }

        if (!TryGetProfileHeightInches(out var heightInches, out var heightError))
        {
            ProfileStatusMessage = heightError;
            return;
        }

        IsSavingProfile = true;
        ProfileStatusMessage = string.Empty;

        try
        {
            var updated = await _api.UpdatePatientDemographicsAsync(
                patient.PatientId,
                new
                {
                    gender = string.IsNullOrWhiteSpace(ProfileGender)
                        ? null
                        : ProfileGender,
                    height_inches = heightInches,
                });

            if (updated is null)
            {
                ProfileStatusMessage = "Could not update the patient profile.";
                return;
            }

            _patientState.ApplyUpdatedPatient(updated);
            LoadPatientProfile();
            ProfileStatusMessage = "Patient profile updated.";
        }
        finally
        {
            IsSavingProfile = false;
        }
    }

    private bool TryGetProfileHeightInches(
        out int? totalInches,
        out string error)
    {
        totalInches = null;
        error = string.Empty;

        var feetText = ProfileHeightFeet?.Trim() ?? string.Empty;
        var inchesText = ProfileHeightInches?.Trim() ?? string.Empty;

        // Clearing both fields intentionally clears the stored height.
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

    private void ApplyPreferences(UserPreferences preferences)
    {
        _suppressPreferenceSave = true;
        try
        {
            CurrentTheme = preferences.Theme;
            ShowHeartRate = preferences.ShowHeartRate;
            ShowSpo2 = preferences.ShowSpo2;
            ShowTemperature = preferences.ShowTemperature;
            ShowWeight = preferences.ShowWeight;
            ShowGlucose = preferences.ShowGlucose;
        }
        finally
        {
            _suppressPreferenceSave = false;
        }

        ThemeService.Apply(CurrentTheme);
        OnPropertyChanged(nameof(ThemeDarkColor));
        OnPropertyChanged(nameof(ThemeLightColor));
        OnPropertyChanged(nameof(ThemeVitalsBlueColor));
        OnPropertyChanged(nameof(ThemeSystemColor));
    }

    [RelayCommand]
    async Task SetTheme(string theme)
    {
        CurrentTheme = theme;
        Preferences.Set("theme", theme);
        ThemeService.Apply(theme);
        OnPropertyChanged(nameof(ThemeDarkColor));
        OnPropertyChanged(nameof(ThemeLightColor));
        OnPropertyChanged(nameof(ThemeVitalsBlueColor));
        OnPropertyChanged(nameof(ThemeSystemColor));
        await SavePreferencesAsync();
    }

    /// <summary>
    /// Uses Shell.Current.Navigation.PushAsync rather than a named Shell
    /// route (Shell.Current.GoToAsync("//SomeRoute")) — this doesn't
    /// require registering a route in AppShell.xaml first, which I don't
    /// have visibility into here. If a named route for this screen gets
    /// added later, this can switch to GoToAsync to match convention.
    /// </summary>
    [RelayCommand]
    async Task OpenHouseholdInviteAsync()
    {
        var inviteVm = Application.Current!.Handler.MauiContext!
            .Services.GetService<HouseholdInviteViewModel>()!;
        await Shell.Current.Navigation.PushAsync(new HouseholdInvitePage(inviteVm));
    }

    [RelayCommand]
    async Task SignOutAsync()
    {
        var confirm = await Shell.Current.DisplayAlert(
            "Sign Out",
            "Are you sure you want to sign out?",
            "Sign Out", "Cancel");

        if (!confirm) return;

        _auth.SignOut();
        _patientState.Reset();

        var loginVm = Application.Current!.Handler.MauiContext!
            .Services.GetService<LoginViewModel>()!;

        // Was: Application.Current.MainPage = new LoginPage(loginVm);
        // That assignment sets the legacy Application.MainPage property,
        // which conflicts with App.xaml.cs's overridden CreateWindow the
        // next time the OS recreates the Activity (e.g. app backgrounded
        // and reopened) — throws "Both MainPage was set and CreateWindow
        // was overridden to provide a page." AppNavigation.SetRootPage
        // operates on Window.Page instead, which has no such conflict.
        AppNavigation.SetRootPage(new LoginPage(loginVm));
    }

    partial void OnShowHeartRateChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        Preferences.Set("show_heart_rate", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowSpo2Changed(bool value)
    {
        if (_suppressPreferenceSave) return;
        Preferences.Set("show_spo2", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowTemperatureChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        Preferences.Set("show_temperature", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowWeightChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        Preferences.Set("show_weight", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowGlucoseChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        Preferences.Set("show_glucose", value);
        _ = SavePreferencesAsync();
    }

    private async Task SavePreferencesAsync()
    {
        try
        {
            await _preferences.SaveAsync(
                new UserPreferences
                {
                    UserId = _auth.UserId ?? string.Empty,
                    DisplayName = _auth.DisplayName,
                    Theme = CurrentTheme,
                    ShowHeartRate = ShowHeartRate,
                    ShowSpo2 = ShowSpo2,
                    ShowTemperature = ShowTemperature,
                    ShowWeight = ShowWeight,
                    ShowGlucose = ShowGlucose,
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== SAVE PREFS ERROR: {ex.Message}");
        }
    }
}
