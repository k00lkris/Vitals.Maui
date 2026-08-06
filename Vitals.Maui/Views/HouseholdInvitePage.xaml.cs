using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class HouseholdInvitePage : ContentPage
{
    private readonly HouseholdInviteViewModel _vm;

    public HouseholdInvitePage(HouseholdInviteViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }
}
