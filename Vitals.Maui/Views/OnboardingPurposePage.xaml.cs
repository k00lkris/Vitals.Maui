using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingPurposePage : ContentPage
{
    public OnboardingPurposePage(OnboardingPurposeViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnBack = async () => await Navigation.PopAsync();

        // Personalization ("Who are you tracking for?") is now built.
        vm.OnContinue = async () =>
        {
            var personalizationVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingPersonalizationViewModel>()!;
            await Navigation.PushAsync(new OnboardingPersonalizationPage(personalizationVm));
        };
    }
}
