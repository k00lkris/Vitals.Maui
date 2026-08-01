using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly PatientStateService _patientState;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    public LoginViewModel(AuthService auth, PatientStateService patientState)
    {
        _auth = auth;
        _patientState = patientState;
    }

    [RelayCommand]
    public async Task SignInWithGoogleAsync()
    {
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var success = await _auth.SignInWithGoogleAsync();

            if (success)
            {
                AppNavigation.RouteAfterGoogleAuth(_auth.IsNewUser, _patientState);
            }
            else
            {
                StatusMessage = "Sign in failed. Please try again.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            System.Diagnostics.Debug.WriteLine($"=== LOGIN ERROR: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Signs in with email/password. Routes based on the server's actual
    /// is_new_user (now correctly tracking "first login ever" for email
    /// accounts, via the has_logged_in column) — NOT hardcoded to existing-
    /// user routing. A fresh registration's first login after verifying
    /// needs onboarding exactly like a first-time Google signup does;
    /// every login after that correctly goes straight to AppShell.
    /// </summary>
    [RelayCommand]
    public async Task SignInWithEmailAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Enter your email and password.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _auth.LoginWithEmailAsync(Email.Trim(), Password);

            if (result.Success)
            {
                AppNavigation.RouteAfterGoogleAuth(_auth.IsNewUser, _patientState);
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Sign in failed. Please try again.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Resolves the target ViewModel from the DI container and swaps the
    /// root page via AppNavigation, since Login/SignUp live outside Shell
    /// (Shell.Current is null here — Shell only exists once AppShell has
    /// been set post-auth).
    /// </summary>
    [RelayCommand]
    public void NavigateToSignUp()
    {
        var signUpVm = Application.Current!.Handler.MauiContext!
            .Services.GetService<SignUpViewModel>()!;
        AppNavigation.SetRootPage(new SignUpPage(signUpVm));
    }
}