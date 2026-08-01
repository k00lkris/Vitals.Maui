using Vitals.Maui.Services;
using Vitals.Maui.Views;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui
{
    public partial class App : Application
    {
        private readonly PatientStateService _patientState;
        private readonly AuthService _auth;
        private readonly LoginViewModel _loginVm;

        public App(PatientStateService patientState, AuthService auth, LoginViewModel loginVm)
        {
            InitializeComponent();
            _patientState = patientState;
            _auth = auth;
            _loginVm = loginVm;

            // Load saved theme and apply before first page renders
            var theme = Preferences.Get("theme", "vitals_blue");
            ThemeService.Apply(theme);
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            try
            {
                var theme = Preferences.Get("theme", "vitals_blue");
                ThemeService.Apply(theme);

                // Show login page initially, then check session asynchronously
                var loginPage = new LoginPage(_loginVm);
                var window = new Window(loginPage);

                // Check session after window is created
                Task.Run(async () =>
                {
                    var hasSession = await _auth.TryRestoreSessionAsync();
                    if (hasSession)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            // Reset the patient cache before showing anything
                            // authenticated. PatientStateService is a
                            // Singleton — without this, a cold launch that
                            // restores a session would still show whatever
                            // household's patients were cached from an
                            // earlier session in the same process, even
                            // though the auth identity just verified
                            // correctly for a (possibly different) account.
                            _patientState.Reset();

                            if (!Preferences.Get("onboarding_complete", false))
                            {
                                var resumeVm = Application.Current!.Handler!.MauiContext!
                                    .Services.GetService<OnboardingResumePromptViewModel>()!;
                                window.Page = new OnboardingResumePromptPage(resumeVm);
                            }
                            else
                            {
                                window.Page = new AppShell(_patientState);
                            }
                        });
                    }
                });

                return window;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== CREATE WINDOW ERROR: {ex.Message}");
                throw;
            }
        }
    }
}