using Vitals.Maui.ViewModels;
namespace Vitals.Maui.Views;
public partial class OnboardingPurposePage : ContentPage
{
    public OnboardingPurposePage(OnboardingPurposeViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        Preferences.Set("onboarding_last_step", "purpose");
        vm.OnBack = async () => await Navigation.PopAsync();

        // Plan selection (Individual/Family/Free/Join) now runs before
        // Personalization — Family and Join both skip Personalization
        // entirely (see OnboardingPlanSelectionPage), Individual/Free
        // continue to it normally.
        vm.OnContinue = async () =>
        {
            var planVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingPlanSelectionViewModel>()!;
            await Navigation.PushAsync(new OnboardingPlanSelectionPage(planVm));
        };
    }
}