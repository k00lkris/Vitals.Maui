using Vitals.Maui.ViewModels;

namespace Vitals.Maui.Views;

public partial class SignUpPage : ContentPage
{
    private readonly SignUpViewModel _vm;

    public SignUpPage(SignUpViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    /// <summary>
    /// SignUpPage is a bare MainPage (not wrapped in a NavigationPage), so
    /// there's no stack for the Android hardware/gesture back button to pop
    /// — its default behavior with nothing to pop is to close the app
    /// entirely. That's correct on LoginPage (it's the true root screen)
    /// but wrong here, since conceptually "back" from Sign Up should return
    /// to Login. Intercepting it and routing through the same command the
    /// on-screen back arrow uses keeps both paths consistent.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (_vm?.BackToLoginCommand.CanExecute(null) == true)
        {
            _vm.BackToLoginCommand.Execute(null);
        }
        return true; // suppress default back behavior (app exit)
    }
}
