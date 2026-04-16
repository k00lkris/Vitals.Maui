using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class VisitLogPage : ContentPage
{
    private readonly VisitLogViewModel _vm;

    public VisitLogPage(VisitLogViewModel vm)
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