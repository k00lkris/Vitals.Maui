using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingVitalPreferencesPage : ContentPage
{
    public OnboardingVitalPreferencesPage(OnboardingVitalPreferencesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "vital_preferences");

        vm.OnBack = async () => await Navigation.PopAsync();

        vm.OnContinue = async () =>
        {
            var firstReadingVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingFirstVitalReadingViewModel>()!;
            await firstReadingVm.LoadAsync();
            await Navigation.PushAsync(new OnboardingFirstVitalReadingPage(firstReadingVm));
        };
    }
}
