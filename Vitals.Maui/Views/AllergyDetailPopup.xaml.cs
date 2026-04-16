using CommunityToolkit.Maui.Views;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class AllergyDetailPopup : Popup
{
    private readonly AllergyDetailViewModel _vm;

    public AllergyDetailPopup(AllergyDetailViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        _vm.OnSaved = () => MainThread.BeginInvokeOnMainThread(() => Close());
        _vm.OnCancelled = () => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_vm.IsAddMode) Close();
        });
    }

    private void OnCloseClicked(object sender, EventArgs e)
    {
        Close();
    }
}