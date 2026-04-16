using CommunityToolkit.Maui;
using LiveChartsCore.SkiaSharpView.Maui;
using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
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
            .UseMauiCommunityToolkit()
            .UseSkiaSharp()
            .UseLiveCharts()
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
        builder.Services.AddSingleton<PatientStateService>();
        builder.Services.AddSingleton<AppShell>();

        // ViewModels
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<VitalsEntryViewModel>();
        builder.Services.AddSingleton<MedicationsViewModel>();
        builder.Services.AddSingleton<CareTeamViewModel>();
        builder.Services.AddTransient<MedicationDetailViewModel>();
        builder.Services.AddTransient<DoctorDetailViewModel>();
        builder.Services.AddTransient<VitalsHistoryViewModel>();
        builder.Services.AddSingleton<AllergiesViewModel>();
        builder.Services.AddTransient<AllergyDetailViewModel>();
        builder.Services.AddSingleton<GeneratePdfViewModel>();
        builder.Services.AddSingleton<VisitLogViewModel>();
        builder.Services.AddSingleton<VisitLogPage>();
        builder.Services.AddSingleton<IncidentLogViewModel>();
        builder.Services.AddSingleton<IncidentLogPage>();
        builder.Services.AddSingleton<NotesViewModel>();
        builder.Services.AddSingleton<NotesPage>();


        // Views
        builder.Services.AddSingleton<DashboardPage>();
        builder.Services.AddSingleton<VitalsEntryPage>();
        builder.Services.AddSingleton<MedicationsPage>();
        builder.Services.AddSingleton<CareTeamPage>();
        builder.Services.AddTransient<MedicationDetailPopup>();
        builder.Services.AddTransient<DoctorDetailPopup>();
        builder.Services.AddTransient<GeneratePdfPage>();
        builder.Services.AddTransient<VitalsHistoryPage>();
        builder.Services.AddSingleton<AllergiesPage>();
        builder.Services.AddTransient<AllergyDetailPopup>();
        builder.Services.AddSingleton<GeneratePdfPage>();
        builder.Services.AddTransient<VisitDetailViewModel>();
        builder.Services.AddTransient<VisitDetailPopup>();
        builder.Services.AddTransient<IncidentDetailViewModel>();
        builder.Services.AddTransient<IncidentDetailPopup>();
        builder.Services.AddTransient<NoteDetailViewModel>();
        builder.Services.AddTransient<NoteDetailPopup>();



#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}