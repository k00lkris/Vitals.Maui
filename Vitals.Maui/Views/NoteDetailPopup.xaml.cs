using CommunityToolkit.Maui.Views;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class NoteDetailPopup : Popup
{
    private readonly NoteDetailViewModel _vm;

    public NoteDetailPopup(NoteDetailViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        _vm.OnSaved = () => MainThread.BeginInvokeOnMainThread(() => Close());
        _vm.OnCancelled = () => MainThread.BeginInvokeOnMainThread(() => Close());
    }

    private void OnCloseClicked(object sender, EventArgs e) => Close();
}