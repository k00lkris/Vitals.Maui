using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingWelcomeViewModel : ObservableObject
{
    // Wired by the page's code-behind (same OnSaved/OnCancelled callback
    // pattern already used in MedicationDetailViewModel) rather than Shell
    // routing, since this screen runs inside a plain NavigationPage, not
    // Shell — Shell.Current is null pre-authentication.
    public Action? OnContinue { get; set; }

    [RelayCommand]
    public void Continue()
    {
        OnContinue?.Invoke();
    }
}
