using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingWelcomePage : ContentPage
{
    public OnboardingWelcomePage(OnboardingWelcomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnContinue = async () =>
        {
            var purposeVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingPurposeViewModel>()!;
            await Navigation.PushAsync(new OnboardingPurposePage(purposeVm));
        };
    }
}
