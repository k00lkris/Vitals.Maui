using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingSampleDashboardPage : ContentPage
{
    public OnboardingSampleDashboardPage(OnboardingSampleDashboardViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "sample_dashboard");

        vm.OnBack = async () => await Navigation.PopAsync();

        // Vital Preferences is now built.
        vm.OnContinue = async () =>
        {
            var preferencesVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingVitalPreferencesViewModel>()!;
            await Navigation.PushAsync(new OnboardingVitalPreferencesPage(preferencesVm));
        };

        // Auto-scroll so the card matching the current tour step is always
        // in view, since Averages/Analysis can sit below the fold on
        // smaller screens otherwise.
        vm.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName != nameof(OnboardingSampleDashboardViewModel.CurrentStep)) return;

            var target = vm.CurrentStep switch
            {
                0 => HeaderCard,
                1 => LatestReadingCard,
                2 => AveragesCard,
                3 => AnalysisCard,
                _ => null
            };

            if (target is not null)
            {
                await DashboardScrollView.ScrollToAsync(target, ScrollToPosition.Start, animated: true);
            }
        };
    }
}
