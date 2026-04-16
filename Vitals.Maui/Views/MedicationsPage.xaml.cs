using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class MedicationsPage : ContentPage
{
    private readonly MedicationsViewModel _vm;

    public MedicationsPage(MedicationsViewModel vm)
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