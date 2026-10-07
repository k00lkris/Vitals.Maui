namespace Vitals.Maui.Services;

/// <summary>
/// Swaps the visible root page by setting the current Window's Page
/// property — NOT Application.Current.MainPage. This app overrides
/// App.CreateWindow(...) (see App.xaml.cs), and MAUI does not allow both
/// CreateWindow AND Application.MainPage to be used — if MainPage is ever
/// assigned, the next time CreateWindow is invoked (e.g. after Android
/// recreates the Activity following the app being backgrounded/killed),
/// MAUI throws "Both MainPage was set and CreateWindow was overridden to
/// provide a page." Window.Page has no such conflict and is the correct
/// way to swap root pages in an app using CreateWindow.
///
/// Use this everywhere a screen needs to replace the whole visible page
/// (login/logout, sign-up, entering/leaving onboarding) instead of ever
/// setting Application.Current.MainPage directly.
/// </summary>
public static class AppNavigation
{
    public static void SetRootPage(Page page)
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is not null)
        {
            window.Page = page;
        }
        else
        {
            // Should not normally happen (there's always a window by the
            // time user code runs), but avoids silently doing nothing.
            Application.Current!.MainPage = page;
        }
    }

    /// <summary>
    /// Routes after authentication using the server-authoritative entitlement
    /// before exposing the main shell. An over-capacity downgrade must be
    /// resolved first so the app never silently chooses which patients stay
    /// active.
    /// </summary>
    public static async Task RouteAfterAuthAsync(
        bool isNewUser,
        PatientStateService patientState)
    {
        patientState.Reset();

        if (isNewUser)
        {
            var welcomeVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<Vitals.Maui.ViewModels.OnboardingWelcomeViewModel>()!;
            SetRootPage(new NavigationPage(
                new Vitals.Maui.Views.OnboardingWelcomePage(welcomeVm)));
            return;
        }

        // Existing accounts never went through this onboarding flow at all.
        Preferences.Set("onboarding_complete", true);

        var services = Application.Current!.Handler.MauiContext!.Services;
        var entitlements = services.GetService<EntitlementService>()!;
        var entitlement = await entitlements.RefreshAsync();

        if (entitlement?.RequiresBasicPatientSelection == true)
        {
            var selectionPage = services.GetService<Vitals.Maui.Views.PatientAccessSelectionPage>()!;
            SetRootPage(new NavigationPage(selectionPage));
            return;
        }

        ShowMainShell(patientState);
    }

    /// <summary>
    /// Enters the normal authenticated shell after all required entitlement
    /// transitions are resolved. Also refreshes long-lived singleton view
    /// models that may still contain the previous account's state.
    /// </summary>
    public static void ShowMainShell(PatientStateService patientState)
    {
        var services = Application.Current!.Handler.MauiContext!.Services;

        var dashboardVm = services
            .GetService<Vitals.Maui.ViewModels.DashboardViewModel>()!;
        _ = dashboardVm.LoadAsync();

        var settingsVm = services
            .GetService<Vitals.Maui.ViewModels.SettingsViewModel>()!;
        settingsVm.RefreshAccountInfo();

        SetRootPage(new Vitals.Maui.AppShell(patientState));
    }

}
