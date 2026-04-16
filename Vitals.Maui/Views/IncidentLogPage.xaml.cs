using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class IncidentLogPage : ContentPage
{
    private readonly IncidentLogViewModel _vm;

    public IncidentLogPage(IncidentLogViewModel vm)
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