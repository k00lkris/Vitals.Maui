using CommunityToolkit.Maui.Views;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class VisitDetailPopup : Popup
{
    private readonly VisitDetailViewModel _vm;

    public VisitDetailPopup(VisitDetailViewModel vm)
    {
        InitializeComponent();
        ResponsivePopupSizing.Attach(this, PopupRoot, portraitWidth: 360, portraitHeight: 680);
        _vm = vm;
        BindingContext = vm;

        _vm.OnSaved = () => MainThread.BeginInvokeOnMainThread(() => Close());
    }

    private void OnCloseClicked(object sender, EventArgs e) => Close();
}