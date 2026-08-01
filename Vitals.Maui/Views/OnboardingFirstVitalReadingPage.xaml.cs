using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingFirstVitalReadingPage : ContentPage
{
    public OnboardingFirstVitalReadingPage(OnboardingFirstVitalReadingViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "first_reading");

        vm.OnBack = async () => await Navigation.PopAsync();

        // No OnContinue wiring needed here — Submit and Skip both finish
        // onboarding directly (see FinishOnboarding() in the ViewModel),
        // since this is the last screen in the flow.
    }
}
