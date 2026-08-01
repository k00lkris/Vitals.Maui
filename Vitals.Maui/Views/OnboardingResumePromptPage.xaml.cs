using Vitals.Maui.Services;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingResumePromptPage : ContentPage
{
    public OnboardingResumePromptPage(OnboardingResumePromptViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnContinueOnboarding = async () =>
        {
            var services = Application.Current!.Handler!.MauiContext!.Services;

            // Six top-level onboarding screens are tracked (see
            // Preferences.Set("onboarding_last_step", ...) in each page's
            // constructor). Patient setup (Myself/family member) is NOT
            // tracked as its own step — resuming mid-patient-setup returns
            // to Personalization instead, so re-confirming the choice is
            // the worst case rather than risking a duplicate patient record
            // if someone was interrupted between creating a first and
            // second person.
            var lastStep = Preferences.Get("onboarding_last_step", "welcome");

            ContentPage targetPage;

            switch (lastStep)
            {
                case "purpose":
                    targetPage = new OnboardingPurposePage(services.GetService<OnboardingPurposeViewModel>()!);
                    break;

                case "personalization":
                    targetPage = new OnboardingPersonalizationPage(services.GetService<OnboardingPersonalizationViewModel>()!);
                    break;

                case "sample_dashboard":
                    targetPage = new OnboardingSampleDashboardPage(services.GetService<OnboardingSampleDashboardViewModel>()!);
                    break;

                case "vital_preferences":
                    targetPage = new OnboardingVitalPreferencesPage(services.GetService<OnboardingVitalPreferencesViewModel>()!);
                    break;

                case "first_reading":
                    var firstReadingVm = services.GetService<OnboardingFirstVitalReadingViewModel>()!;
                    await firstReadingVm.LoadAsync();
                    targetPage = new OnboardingFirstVitalReadingPage(firstReadingVm);
                    break;

                default: // "welcome" or anything unrecognized
                    targetPage = new OnboardingWelcomePage(services.GetService<OnboardingWelcomeViewModel>()!);
                    break;
            }

            AppNavigation.SetRootPage(new NavigationPage(targetPage));
        };
    }
}
