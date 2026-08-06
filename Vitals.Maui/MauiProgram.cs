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
        // Replace the existing HttpClient registration with this:
        builder.Services.AddSingleton<AuthHeaderHandler>();
        builder.Services.AddSingleton<AuthService>();

        builder.Services.AddSingleton<HttpClient>(sp =>
        {
            var auth = sp.GetRequiredService<AuthService>();
            var handler = new AuthHeaderHandler(auth)
            {
                InnerHandler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(10)
                }
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
        builder.Services.AddTransient<VitalsAnalysisViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<SignUpViewModel>();
        builder.Services.AddTransient<OnboardingWelcomeViewModel>();
        builder.Services.AddTransient<OnboardingPurposeViewModel>();
        builder.Services.AddTransient<OnboardingPersonalizationViewModel>();
        builder.Services.AddTransient<OnboardingSampleDashboardViewModel>();
        builder.Services.AddTransient<OnboardingPatientSetupViewModel>();
        builder.Services.AddTransient<OnboardingVitalPreferencesViewModel>();
        builder.Services.AddTransient<OnboardingFirstVitalReadingViewModel>();
        builder.Services.AddTransient<OnboardingResumePromptViewModel>();
        builder.Services.AddTransient<OnboardingPlanSelectionViewModel>();
        builder.Services.AddTransient<OnboardingJoinHouseholdViewModel>();
        builder.Services.AddTransient<OnboardingJoinPatientSelectionViewModel>();
        builder.Services.AddTransient<HouseholdInviteViewModel>();


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
        builder.Services.AddTransient<VisitDetailViewModel>();
        builder.Services.AddTransient<VisitDetailPopup>();
        builder.Services.AddTransient<IncidentDetailViewModel>();
        builder.Services.AddTransient<IncidentDetailPopup>();
        builder.Services.AddTransient<NoteDetailViewModel>();
        builder.Services.AddTransient<NoteDetailPopup>();
        builder.Services.AddTransient<VitalsAnalysisView>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<SignUpPage>();
        builder.Services.AddTransient<OnboardingWelcomePage>();
        builder.Services.AddTransient<OnboardingPurposePage>();
        builder.Services.AddTransient<OnboardingPersonalizationPage>();
        builder.Services.AddTransient<OnboardingSampleDashboardPage>();
        builder.Services.AddTransient<OnboardingPatientSetupPage>();
        builder.Services.AddTransient<OnboardingVitalPreferencesPage>();
        builder.Services.AddTransient<OnboardingFirstVitalReadingPage>();
        builder.Services.AddTransient<OnboardingResumePromptPage>();
        builder.Services.AddTransient<OnboardingPlanSelectionPage>();
        builder.Services.AddTransient<OnboardingJoinHouseholdPage>();
        builder.Services.AddTransient<OnboardingJoinPatientSelectionPage>();
        builder.Services.AddTransient<HouseholdInvitePage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}