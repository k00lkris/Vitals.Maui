using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingResumePromptViewModel : ObservableObject
{
    private readonly PatientStateService _patientState;

    // Wired by the page's code-behind.
    public Func<Task>? OnContinueOnboarding { get; set; }

    public OnboardingResumePromptViewModel(PatientStateService patientState)
    {
        _patientState = patientState;
    }

    [RelayCommand]
    public async Task ContinueOnboardingAsync()
    {
        if (OnContinueOnboarding is not null)
        {
            await OnContinueOnboarding();
        }
    }

    [RelayCommand]
    public void SkipOnboarding()
    {
        // Flag so the Dashboard shows a one-time reminder that preferences
        // can still be finished in Settings — checked and cleared in
        // DashboardViewModel.LoadAsync().
        Preferences.Set("pending_onboarding_tip", true);
        AppNavigation.SetRootPage(new Vitals.Maui.AppShell(_patientState));
    }
}
