using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly SemaphoreSlim _preferenceSaveLock = new(1, 1);
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
    [ObservableProperty] DateTime _profileHeightEffectiveDate = DateTime.Today;
    [ObservableProperty] bool _isSavingProfile;
    [ObservableProperty] bool _isSavingHeight;
    [ObservableProperty] string _profileStatusMessage = string.Empty;
    [ObservableProperty] string _heightStatusMessage = string.Empty;

    public ObservableCollection<PatientHeightRecord> HeightHistory { get; } = new();

    public bool HasSelectedPatient => _patientState.SelectedPatient is not null;
    public bool HasHeightHistory => HeightHistory.Count > 0;
    public bool CanCorrectHeight => HeightHistory.Any(h => h.IsActive);
    public string ProfileCurrentHeightDisplay =>
        _patientState.SelectedPatient?.HeightDisplay ?? "Not set";

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

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                HeightStatusMessage = string.Empty;
                await LoadSelectedPatientAsync();
            }
        };

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
        ApplyPreferences(
            _preferences.LocalSnapshot(_patientState.SelectedPatient?.PatientId));
    }

    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        await LoadSelectedPatientAsync();
    }

    private async Task LoadSelectedPatientAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;

        if (string.IsNullOrWhiteSpace(patientId))
        {
            HeightHistory.Clear();
            LoadPatientProfile();
            OnPropertyChanged(nameof(HasHeightHistory));
            OnPropertyChanged(nameof(CanCorrectHeight));
            return;
        }

        var preferences = await _preferences.RefreshAsync(patientId);

        if (_patientState.SelectedPatient?.PatientId != patientId)
            return;

        ApplyPreferences(preferences);
        LoadPatientProfile();
        await LoadHeightHistoryAsync(patientId);
    }

    private void LoadPatientProfile()
    {
        var patient = _patientState.SelectedPatient;

        ProfileStatusMessage = string.Empty;
        ProfilePatientName = patient?.FullName ?? "No patient selected";
        ProfileGender = patient?.Gender ?? string.Empty;
        ProfileHeightEffectiveDate = DateTime.Today;

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
        OnPropertyChanged(nameof(ProfileCurrentHeightDisplay));
    }

    private async Task LoadHeightHistoryAsync(string patientId)
    {
        var history = await _api.GetPatientHeightHistoryAsync(patientId);

        if (_patientState.SelectedPatient?.PatientId != patientId)
            return;

        HeightHistory.Clear();
        foreach (var item in history)
            HeightHistory.Add(item);

        OnPropertyChanged(nameof(HasHeightHistory));
        OnPropertyChanged(nameof(CanCorrectHeight));
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

        IsSavingProfile = true;
        ProfileStatusMessage = string.Empty;

        try
        {
            // Height now has its own dated/auditable workflow below. Patient
            // Profile saves only the demographic fields that are not part of
            // the height timeline.
            var updated = await _api.UpdatePatientDemographicsAsync(
                patient.PatientId,
                new
                {
                    gender = string.IsNullOrWhiteSpace(ProfileGender)
                        ? null
                        : ProfileGender,
                });

            if (updated is null)
            {
                ProfileStatusMessage = "Could not update the patient profile.";
                return;
            }

            _patientState.ApplyUpdatedPatient(updated);
            ProfileStatusMessage = "Patient profile updated.";
        }
        finally
        {
            IsSavingProfile = false;
        }
    }

    [RelayCommand]
    async Task RecordHeightMeasurementAsync()
    {
        await SaveHeightAsync(
            updateType: "measurement",
            effectiveDate: ProfileHeightEffectiveDate);
    }

    [RelayCommand]
    async Task CorrectCurrentHeightAsync()
    {
        if (!CanCorrectHeight)
        {
            HeightStatusMessage = "There is no height record to correct yet.";
            return;
        }

        var confirm = await Shell.Current.DisplayAlert(
            "Correct Current Height",
            "Use correction only when the latest stored height was entered incorrectly. " +
            "A real newer measurement should be recorded as a new height instead.",
            "Correct",
            "Cancel");

        if (!confirm)
            return;

        // Null tells the API to preserve the original effective date of the
        // record being corrected.
        await SaveHeightAsync(
            updateType: "correction",
            effectiveDate: null);
    }

    private async Task SaveHeightAsync(string updateType, DateTime? effectiveDate)
    {
        var patient = _patientState.SelectedPatient;
        if (patient is null)
        {
            HeightStatusMessage = "Select a patient first.";
            return;
        }

        if (!TryGetProfileHeightInches(out var heightInches, out var heightError))
        {
            HeightStatusMessage = heightError;
            return;
        }

        if (effectiveDate is DateTime date && date.Date > DateTime.Today)
        {
            HeightStatusMessage = "Height date cannot be in the future.";
            return;
        }

        if (IsSavingHeight)
            return;

        IsSavingHeight = true;
        HeightStatusMessage = string.Empty;

        try
        {
            var saved = await _api.RecordPatientHeightAsync(
                patient.PatientId,
                heightInches,
                effectiveDate,
                updateType);

            if (saved?.Record is null)
            {
                HeightStatusMessage = "Could not save the height record.";
                return;
            }

            // Replace the selected Patient instance so Dashboard/Analysis and
            // any other bindings immediately see the newly synchronized
            // current profile height.
            var updatedPatient = new Patient
            {
                PatientId = patient.PatientId,
                FirstName = patient.FirstName,
                LastName = patient.LastName,
                Dob = patient.Dob,
                Gender = patient.Gender,
                HeightInches = saved.CurrentHeightInches,
            };
            _patientState.ApplyUpdatedPatient(updatedPatient);

            await LoadHeightHistoryAsync(patient.PatientId);

            if (saved.CurrentHeightInches is int currentHeight)
            {
                ProfileHeightFeet = (currentHeight / 12).ToString();
                ProfileHeightInches = (currentHeight % 12).ToString();
            }

            ProfileHeightEffectiveDate = DateTime.Today;
            OnPropertyChanged(nameof(ProfileCurrentHeightDisplay));

            HeightStatusMessage = updateType == "correction"
                ? "Current height corrected. The superseded record remains in history."
                : "New dated height measurement recorded.";
        }
        finally
        {
            IsSavingHeight = false;
        }
    }

    private bool TryGetProfileHeightInches(
        out int totalInches,
        out string error)
    {
        totalInches = 0;
        error = string.Empty;

        var feetText = ProfileHeightFeet?.Trim() ?? string.Empty;
        var inchesText = ProfileHeightInches?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(feetText) && string.IsNullOrEmpty(inchesText))
        {
            error = "Enter a height before recording a measurement.";
            return false;
        }

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
        await _preferences.SaveThemeAsync(theme);
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
        _ = SavePreferencesAsync();
    }

    partial void OnShowSpo2Changed(bool value)
    {
        if (_suppressPreferenceSave) return;
        _ = SavePreferencesAsync();
    }

    partial void OnShowTemperatureChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        _ = SavePreferencesAsync();
    }

    partial void OnShowWeightChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        _ = SavePreferencesAsync();
    }

    partial void OnShowGlucoseChanged(bool value)
    {
        if (_suppressPreferenceSave) return;
        _ = SavePreferencesAsync();
    }

    private async Task SavePreferencesAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (string.IsNullOrWhiteSpace(patientId))
            return;

        // Switch PropertyChanged callbacks are fire-and-forget. Serialize the
        // writes so rapid Weight/Glucose toggles cannot arrive out of order and
        // overwrite one another with stale full-state payloads.
        await _preferenceSaveLock.WaitAsync();
        try
        {
            // Re-check after waiting in case the user switched patients.
            if (_patientState.SelectedPatient?.PatientId != patientId)
                return;

            var requested = new UserPreferences
            {
                UserId = _auth.UserId ?? string.Empty,
                PatientId = patientId,
                DisplayName = _auth.DisplayName,
                Theme = CurrentTheme,
                ShowHeartRate = ShowHeartRate,
                ShowSpo2 = ShowSpo2,
                ShowTemperature = ShowTemperature,
                ShowWeight = ShowWeight,
                ShowGlucose = ShowGlucose,
            };

            var saved = await _preferences.SaveAsync(requested, patientId);
            if (saved)
                return;

            // A failed write must not leave a switch visually ON from only a
            // local cache. Reload the server's actual state and put the UI back
            // in sync so the failure is visible instead of silent.
            var actual = await _preferences.RefreshAsync(patientId);
            if (_patientState.SelectedPatient?.PatientId == patientId)
            {
                ApplyPreferences(actual);
                await Shell.Current.DisplayAlert(
                    "Could Not Save Vital Settings",
                    "Your vital selections were not saved. Please try again.",
                    "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== SAVE PREFS ERROR: {ex.Message}");

            var actual = await _preferences.RefreshAsync(patientId);
            if (_patientState.SelectedPatient?.PatientId == patientId)
                ApplyPreferences(actual);
        }
        finally
        {
            _preferenceSaveLock.Release();
        }
    }
}
