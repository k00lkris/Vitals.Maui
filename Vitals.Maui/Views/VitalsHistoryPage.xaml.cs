using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class VitalsHistoryPage : ContentPage
{
    private readonly VitalsHistoryViewModel _vm;

    public VitalsHistoryPage(VitalsHistoryViewModel vm)
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

    private async void OnCustomDaysClicked(object sender, EventArgs e)
    {
        var result = await DisplayPromptAsync(
            "Custom Range",
            "Enter number of days:",
            "OK",
            "Cancel",
            placeholder: "e.g. 90",
            maxLength: 4,
            keyboard: Keyboard.Numeric);

        if (!string.IsNullOrWhiteSpace(result))
            await _vm.SelectCustomDaysAsync(result);
    }
}