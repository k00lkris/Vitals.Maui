using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class OnboardingFirstVitalReadingPage : ContentPage
{
    private readonly OnboardingFirstVitalReadingViewModel _vm;

    public OnboardingFirstVitalReadingPage(OnboardingFirstVitalReadingViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        Preferences.Set("onboarding_last_step", "first_reading");

        vm.OnBack = async () => await Navigation.PopAsync();

        // Submit and Skip both finish onboarding directly.
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }
}
