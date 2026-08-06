using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ApiService _apiService;
    private readonly AuthService _auth;
    private readonly PatientStateService _patientState;

    [ObservableProperty] string _currentTheme = "vitals_blue";
    [ObservableProperty] bool _showHeartRate = true;
    [ObservableProperty] bool _showSpo2 = true;
    [ObservableProperty] bool _showTemperature = true;
    [ObservableProperty] bool _showWeight = false;
    [ObservableProperty] bool _showGlucose = false;

    public string DisplayName => _auth.DisplayName ?? "Unknown";
    public string Email => _auth.Email ?? "";

    public string ThemeDarkColor => CurrentTheme == "dark" ? "#0f3460" : "Transparent";
    public string ThemeLightColor => CurrentTheme == "light" ? "#0f3460" : "Transparent";
    public string ThemeVitalsBlueColor => CurrentTheme == "vitals_blue" ? "#0f3460" : "Transparent";
    public string ThemeSystemColor => CurrentTheme == "system" ? "#0f3460" : "Transparent";

    public SettingsViewModel(ApiService apiService, AuthService auth, PatientStateService patientState)
    {
        _apiService = apiService;
        _auth = auth;
        _patientState = patientState;
        LoadPreferences();
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
    }

    private void LoadPreferences()
    {
        CurrentTheme = Preferences.Get("theme", "vitals_blue");
        ShowHeartRate = Preferences.Get("show_heart_rate", true);
        ShowSpo2 = Preferences.Get("show_spo2", true);
        ShowTemperature = Preferences.Get("show_temperature", true);
        ShowWeight = Preferences.Get("show_weight", false);
        ShowGlucose = Preferences.Get("show_glucose", false);
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
        Preferences.Set("show_heart_rate", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowSpo2Changed(bool value)
    {
        Preferences.Set("show_spo2", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowTemperatureChanged(bool value)
    {
        Preferences.Set("show_temperature", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowWeightChanged(bool value)
    {
        Preferences.Set("show_weight", value);
        _ = SavePreferencesAsync();
    }

    partial void OnShowGlucoseChanged(bool value)
    {
        Preferences.Set("show_glucose", value);
        _ = SavePreferencesAsync();
    }

    private async Task SavePreferencesAsync()
    {
        try
        {
            var userId = _auth.UserId;
            if (string.IsNullOrEmpty(userId)) return;

            await _apiService.UpdateUserPreferencesAsync(
                userId,
                new
                {
                    theme = CurrentTheme,
                    show_heart_rate = ShowHeartRate,
                    show_spo2 = ShowSpo2,
                    show_temperature = ShowTemperature,
                    show_weight = ShowWeight,
                    show_glucose = ShowGlucose,
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== SAVE PREFS ERROR: {ex.Message}");
        }
    }
}
