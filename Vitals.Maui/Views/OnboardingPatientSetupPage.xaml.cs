using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingPatientSetupPage : ContentPage
{
    public OnboardingPatientSetupPage(OnboardingPatientSetupViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.OnBack = async () => await Navigation.PopAsync();

        // OnContinue is intentionally left for the caller to set (see
        // OnboardingPersonalizationPage.xaml.cs) — where this should go next
        // depends on whether this is the only patient being set up, or the
        // first of two in the "Both" path.
    }
}
