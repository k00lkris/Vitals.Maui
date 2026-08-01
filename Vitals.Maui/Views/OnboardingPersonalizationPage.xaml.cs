using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingPersonalizationPage : ContentPage
{
    public OnboardingPersonalizationPage(OnboardingPersonalizationViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "personalization");

        vm.OnBack = async () => await Navigation.PopAsync();

        // Routes based on the "who are you tracking for" selection:
        // myself/family -> one patient-setup screen -> Sample Dashboard
        // both          -> patient-setup (self) -> patient-setup (family) -> Sample Dashboard
        vm.OnContinue = async () =>
        {
            var services = Application.Current!.Handler.MauiContext!.Services;

            async Task GoToSampleDashboardAsync()
            {
                var dashboardVm = services.GetService<OnboardingSampleDashboardViewModel>()!;
                await Navigation.PushAsync(new OnboardingSampleDashboardPage(dashboardVm));
            }

            OnboardingPatientSetupPage BuildPatientSetupPage(bool isSelf, Func<Task> onDone)
            {
                var setupVm = services.GetService<OnboardingPatientSetupViewModel>()!;
                setupVm.Initialize(isSelf);
                var setupPage = new OnboardingPatientSetupPage(setupVm);
                setupVm.OnContinue = async () => await onDone();
                return setupPage;
            }

            switch (vm.Selection)
            {
                case "myself":
                    await Navigation.PushAsync(BuildPatientSetupPage(isSelf: true, GoToSampleDashboardAsync));
                    break;

                case "family":
                    await Navigation.PushAsync(BuildPatientSetupPage(isSelf: false, GoToSampleDashboardAsync));
                    break;

                case "both":
                    // "Both" chains self -> family -> Sample Dashboard. The
                    // second page (family member) is only constructed once
                    // the first one actually completes, since its OnContinue
                    // needs to push to it.
                    Task GoToFamilyStepAsync()
                    {
                        return Navigation.PushAsync(
                            BuildPatientSetupPage(isSelf: false, GoToSampleDashboardAsync));
                    }
                    await Navigation.PushAsync(BuildPatientSetupPage(isSelf: true, GoToFamilyStepAsync));
                    break;
            }
        };
    }
}
