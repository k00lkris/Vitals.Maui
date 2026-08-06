using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingJoinHouseholdViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly AuthService _auth;

    // Wired by the page's code-behind.
    public Action? OnBack { get; set; }
    public Action<bool>? OnJoined { get; set; }  // bool = can create a new patient

    [ObservableProperty] private string _inviteCode = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingJoinHouseholdViewModel(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
    }

    [RelayCommand]
    public async Task JoinAsync()
    {
        if (string.IsNullOrWhiteSpace(InviteCode))
        {
            StatusMessage = "Enter the invite code from your email.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _api.JoinHouseholdAsync(InviteCode.Trim());

            if (result is null)
            {
                StatusMessage = "Something went wrong. Please try again.";
                return;
            }

            if (!result.Success)
            {
                StatusMessage = result.ErrorMessage ?? "That code didn't work. Please try again.";
                return;
            }

            await _auth.UpdateSessionAsync(result.Token, result.HouseholdId);
            OnJoined?.Invoke(result.CanCreateNewPatient);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
