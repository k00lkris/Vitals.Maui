using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class VitalsEntryPage : ContentPage
{
    private readonly VitalsEntryViewModel _vm;

    public VitalsEntryPage(VitalsEntryViewModel vm)
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