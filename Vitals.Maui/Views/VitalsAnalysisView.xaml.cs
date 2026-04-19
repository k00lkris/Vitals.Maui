using CommunityToolkit.Maui.Views;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class VitalsAnalysisView : Popup
{
    private readonly VitalsAnalysisViewModel _vm;

    public VitalsAnalysisView(VitalsAnalysisViewModel vm)  // ? must match class name
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private void OnCloseClicked(object sender, EventArgs e) => Close();

    private async void OnCustomDaysClicked(object sender, EventArgs e)
    {
        var result = await Shell.Current.CurrentPage.DisplayPromptAsync(
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