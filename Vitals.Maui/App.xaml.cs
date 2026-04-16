using Vitals.Maui.Services;

namespace Vitals.Maui
{
    public partial class App : Application
    {
        private readonly PatientStateService _patientState;

        public App(PatientStateService patientState)
        {
            InitializeComponent();
            _patientState = patientState;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            try
            {
                return new Window(new AppShell(_patientState));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== CREATE WINDOW ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"=== STACK: {ex.StackTrace}");
                throw;
            }
        }
    }
}