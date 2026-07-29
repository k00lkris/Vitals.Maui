using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingPersonalizationPage : ContentPage
{
    public OnboardingPersonalizationPage(OnboardingPersonalizationViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnBack = async () => await Navigation.PopAsync();

        // Sample Dashboard is now built.
        vm.OnContinue = async () =>
        {
            var dashboardVm = Application.Current!.Handler.MauiContext!
                .Services.GetService<OnboardingSampleDashboardViewModel>()!;
            await Navigation.PushAsync(new OnboardingSampleDashboardPage(dashboardVm));
        };
    }
}
