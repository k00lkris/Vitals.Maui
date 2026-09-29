using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly VitalPreferencesService _preferences;
    private readonly AuthService _auth;
    private readonly PatientStateService _patientState;

    public string CurrentTheme
    {
        get => _preferences.Theme;
        private set => _preferences.Theme = value;
    }

    public bool ShowHeartRate
    {
        get => _preferences.ShowHeartRate;
        set => _preferences.ShowHeartRate = value;
    }

    public bool ShowSpo2
    {
        get => _preferences.ShowSpo2;
        set => _preferences.ShowSpo2 = value;
    }

    public bool ShowTemperature
    {
        get => _preferences.ShowTemperature;
        set => _preferences.ShowTemperature = value;
    }

    public bool ShowWeight
    {
        get => _preferences.ShowWeight;
        set => _preferences.ShowWeight = value;
    }

    public bool ShowGlucose
    {
        get => _preferences.ShowGlucose;
        set => _preferences.ShowGlucose = value;
    }

    public string DisplayName => _auth.DisplayName ?? "Unknown";
    public string Email => _auth.Email ?? "";

    // Maps the raw backend value ("password", "google.com", "apple.com")
    // to what's actually shown on screen.
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
        VitalPreferencesService preferences,
        AuthService auth,
        PatientStateService patientState)
    {
        _preferences = preferences;
        _auth = auth;
        _patientState = patientState;

        _preferences.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VitalPreferencesService.Theme))
            {
                OnPropertyChanged(nameof(CurrentTheme));
                OnPropertyChanged(nameof(ThemeDarkColor));
                OnPropertyChanged(nameof(ThemeLightColor));
                OnPropertyChanged(nameof(ThemeVitalsBlueColor));
                OnPropertyChanged(nameof(ThemeSystemColor));
                return;
            }

            if (e.PropertyName is nameof(VitalPreferencesService.ShowHeartRate)
                or nameof(VitalPreferencesService.ShowSpo2)
                or nameof(VitalPreferencesService.ShowTemperature)
                or nameof(VitalPreferencesService.ShowWeight)
                or nameof(VitalPreferencesService.ShowGlucose))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };
    }

    /// <summary>
    /// DisplayName/Email are computed pass-throughs to AuthService. This VM is
    /// a singleton, so account switches must explicitly tell those bindings to
    /// re-read the current AuthService values.
    /// </summary>
    public void RefreshAccountInfo()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(AuthProviderDisplay));
    }

    public Task LoadPreferencesAsync(bool forceRefresh = false) =>
        _preferences.LoadAsync(forceRefresh);

    [RelayCommand]
    Task SetTheme(string theme)
    {
        CurrentTheme = theme;
        return Task.CompletedTask;
    }

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

        _preferences.Reset();
        _auth.SignOut();
        _patientState.Reset();

        var loginVm = Application.Current!.Handler.MauiContext!
            .Services.GetService<LoginViewModel>()!;

        AppNavigation.SetRootPage(new LoginPage(loginVm));
    }
}
