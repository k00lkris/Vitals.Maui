using Android.App;
using Android.Content.PM;
using Microsoft.Maui.Authentication;

namespace Vitals.Maui;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    new[] { global::Android.Content.Intent.ActionView },
    Categories = new[]
    {
        global::Android.Content.Intent.CategoryDefault,
        global::Android.Content.Intent.CategoryBrowsable
    },
    DataScheme = "com.googleusercontent.apps.244707102522-82ebbk0e83e4em79sed7pivc977b2lfk",
    DataPath = "/oauth2redirect")]
public class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}