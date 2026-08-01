using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class SignUpViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly PatientStateService _patientState;

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Set true once registration succeeds — the account exists but isn't
    // usable yet (email unverified), so the page switches to a "check your
    // email" state instead of navigating anywhere.
    [ObservableProperty] private bool _isVerificationSent;

    // Incremented every time a Google sign-in attempt starts, and captured
    // locally by that attempt. WebAuthenticator.AuthenticateAsync() has no
    // cancellation support — if the user backs out, the native call can
    // keep running (or, per a known upstream issue, hang indefinitely and
    // never return at all). If a late/stale result does eventually arrive
    // after the user has already backed out, this token lets us recognize
    // it's stale and ignore it instead of navigating out from under
    // whatever screen the user is actually looking at by then.
    private int _signUpOperationId;

    public SignUpViewModel(AuthService auth, PatientStateService patientState)
    {
        _auth = auth;
        _patientState = patientState;
    }

    /// <summary>
    /// Registers a new email/password account. On success, the account is
    /// NOT signed in — it switches to a "check your email" state, since
    /// /api/auth/register deliberately doesn't return a session until the
    /// email is verified (see AuthService.RegisterAsync).
    /// </summary>
    [RelayCommand]
    public async Task CreateAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            StatusMessage = "Enter your name.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Email))
        {
            StatusMessage = "Enter your email address.";
            return;
        }
        if (Password.Length < 8)
        {
            StatusMessage = "Password must be at least 8 characters.";
            return;
        }
        if (Password != ConfirmPassword)
        {
            StatusMessage = "Passwords don't match.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _auth.RegisterAsync(Email.Trim(), Password, DisplayName.Trim());

            if (result.Success)
            {
                IsVerificationSent = true;
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Something went wrong. Please try again.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ResendVerificationAsync()
    {
        IsBusy = true;
        try
        {
            await _auth.ResendVerificationAsync(Email.Trim());
            StatusMessage = "If that email has a pending verification, a new link has been sent.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reuses the same Google sign-in flow as LoginViewModel — signing up
    /// and signing in with Google are the same backend call (/api/auth/google
    /// upserts by firebase_uid OR email), so there is no separate
    /// "register with Google" endpoint needed. Routes via
    /// AppNavigation.RouteAfterGoogleAuth based on what the backend actually
    /// says about the account (is_new_user) — NOT always to onboarding —
    /// since an existing user could end up on this screen too (e.g. they
    /// meant to tap Sign In) and should land in AppShell like normal, not
    /// get routed through onboarding again.
    ///
    /// Races the actual sign-in against a timeout, since AuthenticateAsync
    /// can hang forever on some devices if the user backs out of the
    /// browser without it registering as a cancellation. This guarantees
    /// IsBusy always clears eventually even in that worst case.
    /// </summary>
    [RelayCommand]
    public async Task SignUpWithGoogleAsync()
    {
        if (IsBusy) return;

        var operationId = ++_signUpOperationId;
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var signInTask = _auth.SignInWithGoogleAsync();

            // If we abandon this task via the timeout below, it keeps running
            // in the background. Make sure a fault in it doesn't surface as
            // an unobserved task exception later.
            _ = signInTask.ContinueWith(t => { var _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(90));
            var completed = await Task.WhenAny(signInTask, timeoutTask);

            // A newer attempt started (or the user already backed out and
            // BackToLogin already reset things) — this result is stale,
            // don't act on it regardless of what it turns out to be.
            if (operationId != _signUpOperationId) return;

            if (completed == timeoutTask)
            {
                StatusMessage = "Sign-in is taking longer than expected. Please try again.";
                return;
            }

            var success = await signInTask;
            if (success)
            {
                AppNavigation.RouteAfterGoogleAuth(_auth.IsNewUser, _patientState);
            }
            else
            {
                StatusMessage = "Sign up failed. Please try again.";
            }
        }
        catch (Exception ex)
        {
            if (operationId != _signUpOperationId) return;
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            if (operationId == _signUpOperationId)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Same DI-resolve + AppNavigation.SetRootPage pattern as
    /// LoginViewModel.NavigateToSignUp. If a Google sign-in is still in
    /// progress, treat back as cancelling it from the UI's perspective:
    /// bump the operation id (so a late/stale result from the still-running
    /// native call gets ignored — see SignUpWithGoogleAsync) and reset
    /// IsBusy/StatusMessage before navigating, so the user is never left
    /// looking at a stuck spinner.
    /// </summary>
    [RelayCommand]
    public void BackToLogin()
    {
        if (IsBusy)
        {
            _signUpOperationId++;
            IsBusy = false;
            StatusMessage = string.Empty;
        }

        var loginVm = Application.Current!.Handler.MauiContext!
            .Services.GetService<LoginViewModel>()!;
        AppNavigation.SetRootPage(new LoginPage(loginVm));
    }
}
