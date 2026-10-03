using CommunityToolkit.Maui.Views;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class DoctorDetailPopup : Popup
{
    private readonly DoctorDetailViewModel _vm;

    public DoctorDetailPopup(DoctorDetailViewModel vm)
    {
        InitializeComponent();
        ResponsivePopupSizing.Attach(this, PopupRoot, portraitWidth: 360, portraitHeight: 640);
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