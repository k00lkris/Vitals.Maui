using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class HouseholdInviteViewModel : ObservableObject
{
    private readonly ApiService _api;

    [ObservableProperty] private string _inviteeEmail = string.Empty;
    [ObservableProperty] private bool _canInvite = true;
    [ObservableProperty] private int _availableSlots;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ObservableCollection<PendingInvite> _pendingInvites = new();

    public HouseholdInviteViewModel(ApiService api)
    {
        _api = api;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var status = await _api.GetHouseholdStatusAsync();
            if (status is not null)
            {
                CanInvite = status.CanInvite;
                AvailableSlots = status.AvailableSlots;
            }

            var invites = await _api.GetPendingInvitesAsync();
            PendingInvites = new ObservableCollection<PendingInvite>(invites);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SendInviteAsync()
    {
        if (string.IsNullOrWhiteSpace(InviteeEmail))
        {
            StatusMessage = "Enter an email address.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _api.CreateHouseholdInviteAsync(InviteeEmail.Trim());
            if (result.Success)
            {
                StatusMessage = $"Invite sent to {InviteeEmail.Trim()}.";
                InviteeEmail = string.Empty;
                await LoadAsync(); // refresh status + pending list
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Something went wrong. Please try again.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task CancelInviteAsync(PendingInvite invite)
    {
        IsBusy = true;
        try
        {
            var success = await _api.CancelInviteAsync(invite.InviteId);
            if (success)
            {
                await LoadAsync(); // refresh status + pending list — frees the slot
            }
            else
            {
                StatusMessage = "Couldn't cancel that invite. Please try again.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
