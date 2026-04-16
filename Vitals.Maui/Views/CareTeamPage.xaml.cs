using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class CareTeamPage : ContentPage
{
    private readonly CareTeamViewModel _vm;

    public CareTeamPage(CareTeamViewModel vm)
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