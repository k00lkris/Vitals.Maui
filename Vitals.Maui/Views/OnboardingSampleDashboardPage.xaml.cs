using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingSampleDashboardPage : ContentPage
{
    public OnboardingSampleDashboardPage(OnboardingSampleDashboardViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnBack = async () => await Navigation.PopAsync();

        // First Log Prompt screen doesn't exist yet — wire this to push it
        // once it's built. Left as a visible TODO rather than a silent no-op.
        vm.OnContinue = () =>
        {
            System.Diagnostics.Debug.WriteLine("=== TODO: push First Log Prompt page once built");
        };
    }
}
