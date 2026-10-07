using Vitals.Maui.Services;
using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class PatientAccessSelectionPage : ContentPage
{
    private readonly PatientAccessSelectionViewModel _vm;
    private readonly PatientStateService _patientState;

    public PatientAccessSelectionPage(
        PatientAccessSelectionViewModel vm,
        PatientStateService patientState)
    {
        InitializeComponent();
        BindingContext = vm;
        _vm = vm;
        _patientState = patientState;

        _vm.OnCompleted = () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
                AppNavigation.ShowMainShell(_patientState));
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }

    private void OnPatientCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.BindingContext is not PatientAccessChoice choice)
        {
            return;
        }

        var accepted = _vm.SetSelection(choice, e.Value);
        if (!accepted && checkBox.IsChecked != choice.IsSelected)
        {
            checkBox.IsChecked = choice.IsSelected;
        }
    }
}
