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
    [ObservableProperty] private bool _canManageHousehold = true;
    [ObservableProperty] private int _availableSlots;
    [ObservableProperty] private bool _isUnlimited;
    [ObservableProperty] private string _slotSummary = string.Empty;
    [ObservableProperty] private string _inviteRestrictionMessage = string.Empty;
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
                CanManageHousehold = status.CanManageHousehold;
                CanInvite = status.CanInvite;
                IsUnlimited = status.IsUnlimited;
                AvailableSlots = status.AvailableSlots ?? 0;

                if (!status.CanManageHousehold)
                {
                    SlotSummary = "Household member management is owner/manager controlled.";
                    InviteRestrictionMessage =
                        "Your current household role and plan do not allow member management.";
                }
                else
                {
                    SlotSummary = status.IsUnlimited
                        ? "Unlimited patient slots"
                        : $"{AvailableSlots} patient slot(s) available for new invites";
                    InviteRestrictionMessage = status.CanInvite
                        ? string.Empty
                        : "No patient slots available — cancel a pending invite below, or wait for one to expire, to invite someone new.";
                }
            }

            if (CanManageHousehold)
            {
                var invites = await _api.GetPendingInvitesAsync();
                PendingInvites = new ObservableCollection<PendingInvite>(invites);
            }
            else
            {
                PendingInvites.Clear();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SendInviteAsync()
    {
        if (!CanManageHousehold)
        {
            StatusMessage = "Your current household role and plan do not allow member management.";
            return;
        }

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
        if (!CanManageHousehold)
        {
            StatusMessage = "Your current household role and plan do not allow member management.";
            return;
        }

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
