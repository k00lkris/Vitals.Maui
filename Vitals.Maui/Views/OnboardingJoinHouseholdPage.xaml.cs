using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingJoinHouseholdPage : ContentPage
{
    public OnboardingJoinHouseholdPage(OnboardingJoinHouseholdViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "join_household");

        vm.OnBack = async () => await Navigation.PopAsync();
        vm.OnJoined = async (canCreateNewPatient) =>
        {
            var services = Application.Current!.Handler.MauiContext!.Services;
            var selectionVm = services.GetService<OnboardingJoinPatientSelectionViewModel>()!;
            await selectionVm.LoadAsync(canCreateNewPatient);
            await Navigation.PushAsync(new OnboardingJoinPatientSelectionPage(selectionVm));
        };
    }
}
