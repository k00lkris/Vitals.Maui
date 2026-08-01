using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingWelcomePage : ContentPage
{
    public OnboardingWelcomePage(OnboardingWelcomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        // Tracked so a session that got interrupted mid-onboarding can
        // resume near here instead of restarting from scratch — see
        // OnboardingResumePromptPage.
        Preferences.Set("onboarding_last_step", "welcome");

        vm.OnContinue = async () =>
        {
            var purposeVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingPurposeViewModel>()!;
            await Navigation.PushAsync(new OnboardingPurposePage(purposeVm));
        };
    }
}
