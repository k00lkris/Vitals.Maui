using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingPurposeViewModel : ObservableObject
{
    // Wired by the page's code-behind, same pattern as OnboardingWelcomeViewModel.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    [RelayCommand]
    public void Continue()
    {
        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
