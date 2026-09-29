using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly PatientStateService _patientState;
    private readonly VitalPreferencesService _vitalPreferences;

    public string DisplayName => _auth.DisplayName ?? "Unknown";
    public string Email => _auth.Email ?? "";

    public string AuthProviderDisplay => _auth.AuthProvider switch
    {
        "password" => "Email",
        "google.com" => "Google",
        "apple.com" => "Apple",
        _ => "Unknown",
    };

    public string CurrentTheme => _vitalPreferences.Theme;

    public bool ShowHeartRate
    {
        get => _vitalPreferences.ShowHeartRate;
        set
        {
            if (value == _vitalPreferences.ShowHeartRate) return;
            _ = _vitalPreferences.SetShowHeartRateAsync(value);
        }
    }

    public bool ShowSpo2
    {
        get => _vitalPreferences.ShowSpo2;
        set
        {
            if (value == _vitalPreferences.ShowSpo2) return;
            _ = _vitalPreferences.SetShowSpo2Async(value);
        }
    }

    public bool ShowTemperature
    {
        get => _vitalPreferences.ShowTemperature;
        set
        {
            if (value == _vitalPreferences.ShowTemperature) return;
            _ = _vitalPreferences.SetShowTemperatureAsync(value);
        }
    }

    public bool ShowWeight
    {
        get => _vitalPreferences.ShowWeight;
        set
        {
            if (value == _vitalPreferences.ShowWeight) return;
            _ = _vitalPreferences.SetShowWeightAsync(value);
        }
    }

    public bool ShowGlucose
    {
        get => _vitalPreferences.ShowGlucose;
        set
        {
            if (value == _vitalPreferences.ShowGlucose) return;
            _ = _vitalPreferences.SetShowGlucoseAsync(value);
        }
    }

    public string ThemeDarkColor => CurrentTheme == "dark" ? "#0f3460" : "Transparent";
    public string ThemeLightColor => CurrentTheme == "light" ? "#0f3460" : "Transparent";
    public string ThemeVitalsBlueColor => CurrentTheme == "vitals_blue" ? "#0f3460" : "Transparent";
    public string ThemeSystemColor => CurrentTheme == "system" ? "#0f3460" : "Transparent";

    public SettingsViewModel(
        AuthService auth,
        PatientStateService patientState,
        VitalPreferencesService vitalPreferences)
    {
        _auth = auth;
        _patientState = patientState;
        _vitalPreferences = vitalPreferences;

        _vitalPreferences.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VitalPreferencesService.Theme):
                    OnPropertyChanged(nameof(CurrentTheme));
                    OnPropertyChanged(nameof(ThemeDarkColor));
                    OnPropertyChanged(nameof(ThemeLightColor));
                    OnPropertyChanged(nameof(ThemeVitalsBlueColor));
                    OnPropertyChanged(nameof(ThemeSystemColor));
                    break;
                case nameof(VitalPreferencesService.ShowHeartRate):
                    OnPropertyChanged(nameof(ShowHeartRate));
                    break;
                case nameof(VitalPreferencesService.ShowSpo2):
                    OnPropertyChanged(nameof(ShowSpo2));
                    break;
                case nameof(VitalPreferencesService.ShowTemperature):
                    OnPropertyChanged(nameof(ShowTemperature));
                    break;
                case nameof(VitalPreferencesService.ShowWeight):
                    OnPropertyChanged(nameof(ShowWeight));
                    break;
                case nameof(VitalPreferencesService.ShowGlucose):
                    OnPropertyChanged(nameof(ShowGlucose));
                    break;
            }
        };
    }

    /// <summary>
    /// Refreshes account identity fields and rehydrates preferences from the
    /// authenticated user's row. The service applies a per-user local cache
    /// first for offline/startup continuity, then replaces it with server
    /// values when the API is reachable.
    /// </summary>
    public async Task LoadAsync()
    {
        RefreshAccountInfo();
        await _vitalPreferences.LoadAsync(forceRefresh: true);
    }

    public void RefreshAccountInfo()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(AuthProviderDisplay));
    }

    [RelayCommand]
    async Task SetTheme(string theme)
    {
        await _vitalPreferences.SetThemeAsync(theme);
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

        _auth.SignOut();
        _patientState.Reset();
        _vitalPreferences.Reset();

        var loginVm = Application.Current!.Handler.MauiContext!
            .Services.GetService<LoginViewModel>()!;

        AppNavigation.SetRootPage(new LoginPage(loginVm));
    }
}
