using CommunityToolkit.Mvvm.ComponentModel;

namespace Vitals.Maui.Services;

/// <summary>
/// In-process cache for the server-authoritative household entitlement.
///
/// 0.7.1b establishes the access-state foundation only. Pages do not gate
/// themselves from this service yet; later Phase 7 components will consume
/// Current/HasPremiumAccess from one place instead of duplicating plan logic
/// across view models.
/// </summary>
public partial class EntitlementService : ObservableObject
{
    private readonly ApiService _api;

    [ObservableProperty]
    private HouseholdEntitlement? _current;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isRefreshing;

    public EntitlementService(ApiService api)
    {
        _api = api;
    }

    public bool HasPremiumAccess => Current?.HasPremiumAccess == true;
    public bool IsFounder => Current?.IsFounder == true;
    public bool IsUnlimited => Current?.IsUnlimited == true;
    public string EffectivePlan => Current?.EffectivePlan ?? string.Empty;
    public string AccessState => Current?.AccessState ?? string.Empty;

    public async Task<HouseholdEntitlement?> RefreshAsync()
    {
        if (IsRefreshing)
            return Current;

        IsRefreshing = true;
        try
        {
            var entitlement = await _api.GetHouseholdEntitlementAsync();
            if (entitlement is not null)
            {
                Current = entitlement;
                IsLoaded = true;
            }

            return Current;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void Reset()
    {
        Current = null;
        IsLoaded = false;
        IsRefreshing = false;
    }

    partial void OnCurrentChanged(HouseholdEntitlement? value)
    {
        OnPropertyChanged(nameof(HasPremiumAccess));
        OnPropertyChanged(nameof(IsFounder));
        OnPropertyChanged(nameof(IsUnlimited));
        OnPropertyChanged(nameof(EffectivePlan));
        OnPropertyChanged(nameof(AccessState));
    }
}
