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
    /// Routes to onboarding or AppShell based on what /api/auth/google
    /// actually said about the account (AuthService.IsNewUser), not which
    /// button the user tapped. Both LoginViewModel.SignInWithGoogleAsync
    /// and SignUpViewModel.SignUpWithGoogleAsync call this after a
    /// successful sign-in so a new user hitting "Sign in" on the Login
    /// page still gets onboarding, and an existing user hitting "Sign up"
    /// on the Sign Up page still lands in AppShell.
    ///
    /// Always resets PatientStateService first. It's a Singleton (lives
    /// for the whole app process), so without this, signing in as a
    /// different account without an app restart would keep showing
    /// whichever household's patients were already cached from a previous
    /// session — the auth identity would correctly switch, but the visible
    /// data wouldn't. Resetting costs one extra API re-fetch even when it
    /// turns out to be the same account; not resetting risks showing one
    /// household's data under a different one's identity.
    /// </summary>
    public static void RouteAfterGoogleAuth(bool isNewUser, PatientStateService patientState)
    {
        patientState.Reset();

        if (isNewUser)
        {
            var welcomeVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<Vitals.Maui.ViewModels.OnboardingWelcomeViewModel>()!;
            SetRootPage(new NavigationPage(new Vitals.Maui.Views.OnboardingWelcomePage(welcomeVm)));
        }
        else
        {
            // Existing accounts never went through this onboarding flow at
            // all — without this, they'd default to onboarding_complete =
            // false and wrongly get the "Welcome back, finish setup?"
            // prompt on their next launch for something they never started.
            Preferences.Set("onboarding_complete", true);
            SetRootPage(new Vitals.Maui.AppShell(patientState));
        }
    }
}
