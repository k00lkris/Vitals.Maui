using CommunityToolkit.Maui.Views;
using Vitals.Maui.Services;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class VitalHistoryDetailPopup : Popup
{
    private readonly VitalHistoryDetailViewModel _vm;

    public VitalHistoryDetailPopup(VitalHistoryDetailViewModel vm)
    {
        InitializeComponent();
        ResponsivePopupSizing.Attach(
            this,
            PopupRoot,
            portraitWidth: 390,
            portraitHeight: 720);

        _vm = vm;
        BindingContext = vm;

        _vm.OnSaved = () =>
            MainThread.BeginInvokeOnMainThread(() => Close());

        _vm.OnCancelled = () =>
            MainThread.BeginInvokeOnMainThread(() => Close());
    }
}
