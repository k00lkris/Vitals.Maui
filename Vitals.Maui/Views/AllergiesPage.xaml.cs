using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class AllergiesPage : ContentPage
{
    private readonly AllergiesViewModel _vm;

    public AllergiesPage(AllergiesViewModel vm)
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