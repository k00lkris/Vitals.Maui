using Vitals.Maui.Services;

namespace Vitals.Maui;

public partial class AppShell : Shell
{
    private readonly PatientStateService _patientState;

    public AppShell(PatientStateService patientState)
    {
        _patientState = patientState;
        BindingContext = patientState;
        InitializeComponent();
        _ = patientState.InitializeAsync();
    }
}