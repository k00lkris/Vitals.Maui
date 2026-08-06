using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingPlanSelectionPage : ContentPage
{
    public OnboardingPlanSelectionPage(OnboardingPlanSelectionViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "plan_selection");

        vm.OnBack = async () => await Navigation.PopAsync();

        vm.OnIndividualOrFreeSelected = async () =>
        {
            var services = Application.Current!.Handler.MauiContext!.Services;
            var personalizationVm = services.GetService<OnboardingPersonalizationViewModel>()!;
            await Navigation.PushAsync(new OnboardingPersonalizationPage(personalizationVm));
        };

        // Family already answers "who are you tracking for" (more than one
        // person) — same self -> family-member -> Sample Dashboard chain
        // Personalization's "both" option uses, just triggered from here
        // instead, skipping the "who are you tracking for" question entirely.
        vm.OnFamilySelected = async () =>
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

            Task GoToFamilyStepAsync()
            {
                return Navigation.PushAsync(BuildPatientSetupPage(isSelf: false, GoToSampleDashboardAsync));
            }

            await Navigation.PushAsync(BuildPatientSetupPage(isSelf: true, GoToFamilyStepAsync));
        };

        vm.OnJoinSelected = async () =>
        {
            var services = Application.Current!.Handler.MauiContext!.Services;
            var joinVm = services.GetService<OnboardingJoinHouseholdViewModel>()!;
            await Navigation.PushAsync(new OnboardingJoinHouseholdPage(joinVm));
        };
    }
}
