using Microsoft.Extensions.Logging;
using Vitals.Maui.Services;
using Vitals.Maui.ViewModels;
using Vitals.Maui.Views;

namespace Vitals.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Services
        builder.Services.AddSingleton<HttpClient>(sp =>
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            return new HttpClient(handler)
            {
                BaseAddress = new Uri(AppConfig.BaseUrl),
                Timeout = TimeSpan.FromSeconds(15)
            };
        });

        builder.Services.AddSingleton<ApiService>();

        // ViewModels
        builder.Services.AddSingleton<VitalsEntryViewModel>();

        // Views
        builder.Services.AddSingleton<VitalsEntryPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}