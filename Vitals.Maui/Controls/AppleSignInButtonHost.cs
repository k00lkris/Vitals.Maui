using System.Windows.Input;
#if IOS
using AuthenticationServices;
using UIKit;
#endif

namespace Vitals.Maui.Controls;

/// <summary>
/// Hosts Apple's system-provided Sign in with Apple button on iOS so the
/// app gets Apple's approved appearance, localization, proportions, and
/// accessibility behavior. On non-iOS targets the control stays hidden.
/// </summary>
public class AppleSignInButtonHost : ContentView
{
    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(
            nameof(Command),
            typeof(ICommand),
            typeof(AppleSignInButtonHost));

    public static readonly BindableProperty IsSignUpProperty =
        BindableProperty.Create(
            nameof(IsSignUp),
            typeof(bool),
            typeof(AppleSignInButtonHost),
            false,
            propertyChanged: OnButtonTypeChanged);

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsSignUp
    {
        get => (bool)GetValue(IsSignUpProperty);
        set => SetValue(IsSignUpProperty, value);
    }

#if IOS
    private ASAuthorizationAppleIdButton? _nativeButton;
#endif

    public AppleSignInButtonHost()
    {
        HeightRequest = 52;

#if IOS
        IsVisible = OperatingSystem.IsIOSVersionAtLeast(13);
        HandlerChanged += OnHandlerChanged;
        HandlerChanging += OnHandlerChanging;
#else
        IsVisible = false;
#endif
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

#if IOS
        if (propertyName == nameof(IsEnabled) && _nativeButton is not null)
            _nativeButton.Enabled = IsEnabled;
#endif
    }

    private static void OnButtonTypeChanged(
        BindableObject bindable,
        object oldValue,
        object newValue)
    {
#if IOS
        if (bindable is AppleSignInButtonHost host &&
            host.Handler?.PlatformView is UIView)
        {
            host.InstallNativeButton();
        }
#endif
    }

#if IOS
    private void OnHandlerChanged(object? sender, EventArgs e) =>
        InstallNativeButton();

    private void OnHandlerChanging(object? sender, HandlerChangingEventArgs e) =>
        RemoveNativeButton();

    private void InstallNativeButton()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(13) ||
            Handler?.PlatformView is not UIView hostView)
            return;

        RemoveNativeButton();

        var buttonType = IsSignUp
            ? ASAuthorizationAppleIdButtonType.SignUp
            : ASAuthorizationAppleIdButtonType.SignIn;

        _nativeButton = ASAuthorizationAppleIdButton.Create(
            buttonType,
            ASAuthorizationAppleIdButtonStyle.Black);

        _nativeButton.CornerRadius = 10;
        _nativeButton.Enabled = IsEnabled;
        _nativeButton.TranslatesAutoresizingMaskIntoConstraints = false;
        _nativeButton.TouchUpInside += OnNativeButtonPressed;

        hostView.AddSubview(_nativeButton);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _nativeButton.LeadingAnchor.ConstraintEqualTo(hostView.LeadingAnchor),
            _nativeButton.TrailingAnchor.ConstraintEqualTo(hostView.TrailingAnchor),
            _nativeButton.TopAnchor.ConstraintEqualTo(hostView.TopAnchor),
            _nativeButton.BottomAnchor.ConstraintEqualTo(hostView.BottomAnchor),
        });
    }

    private void RemoveNativeButton()
    {
        if (_nativeButton is null)
            return;

        _nativeButton.TouchUpInside -= OnNativeButtonPressed;
        _nativeButton.RemoveFromSuperview();
        _nativeButton.Dispose();
        _nativeButton = null;
    }

    private void OnNativeButtonPressed(object? sender, EventArgs e)
    {
        if (Command?.CanExecute(null) == true)
            Command.Execute(null);
    }
#endif
}
