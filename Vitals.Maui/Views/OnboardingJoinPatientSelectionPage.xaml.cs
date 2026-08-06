using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingJoinPatientSelectionPage : ContentPage
{
    public OnboardingJoinPatientSelectionPage(OnboardingJoinPatientSelectionViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnBack = async () => await Navigation.PopAsync();

        vm.OnContinue = async () =>
        {
            var services = Application.Current!.Handler.MauiContext!.Services;
            var dashboardVm = services.GetService<OnboardingSampleDashboardViewModel>()!;
            await Navigation.PushAsync(new OnboardingSampleDashboardPage(dashboardVm));
        };

        vm.OnCreateNewPatient = async () =>
        {
            var services = Application.Current!.Handler.MauiContext!.Services;
            var setupVm = services.GetService<OnboardingPatientSetupViewModel>()!;
            setupVm.Initialize(isSelf: true);
            var setupPage = new OnboardingPatientSetupPage(setupVm);

            setupVm.OnContinue = async () =>
            {
                var dashboardVm = services.GetService<OnboardingSampleDashboardViewModel>()!;
                await Navigation.PushAsync(new OnboardingSampleDashboardPage(dashboardVm));
            };

            await Navigation.PushAsync(setupPage);
        };
    }
}
