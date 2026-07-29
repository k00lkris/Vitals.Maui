using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingSampleDashboardViewModel : ObservableObject
{
    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    private const double DimmedOpacity = 0.35;
    private const double ActiveOpacity = 1.0;

    private static readonly Color ActiveStroke = Color.FromArgb("#1976d2");
    private static readonly Color InactiveStroke = Color.FromArgb("#2a3a5c");

    private readonly record struct TourStep(string Title, string Description, string ButtonText);

    // Mirrors the real DashboardPage's section order: header/Enter Vitals,
    // Latest Reading, Averages, Analysis & Charts & History.
    private readonly TourStep[] _steps =
    {
        new("Log a reading anytime",
            "Tap 'Enter Vitals' whenever you want to log a new reading — it only takes a few seconds.",
            "Next"),
        new("Your latest reading, at a glance",
            "The moment you log something, it shows up here — no digging through history to see how things stand right now.",
            "Next"),
        new("Averages over time",
            "See your 15, 30, 45, or 60-day averages. Tap a range to switch between them.",
            "Next"),
        new("Deeper analysis, when you want it",
            "Tap 'Vitals Analysis' for a clinical breakdown of your trends, expand the charts for a visual view, or browse your full history.",
            "Get Started"),
    };

    [ObservableProperty] private int _currentStep = 0; // 0-based index into _steps

    [ObservableProperty] private string _tourTitle = string.Empty;
    [ObservableProperty] private string _tourDescription = string.Empty;
    [ObservableProperty] private string _tourButtonText = string.Empty;
    [ObservableProperty] private string _stepIndicatorText = string.Empty;

    [ObservableProperty] private double _headerOpacity = ActiveOpacity;
    [ObservableProperty] private double _latestReadingOpacity = DimmedOpacity;
    [ObservableProperty] private double _averagesOpacity = DimmedOpacity;
    [ObservableProperty] private double _analysisOpacity = DimmedOpacity;

    [ObservableProperty] private Color _headerStroke = ActiveStroke;
    [ObservableProperty] private Color _latestReadingStroke = InactiveStroke;
    [ObservableProperty] private Color _averagesStroke = InactiveStroke;
    [ObservableProperty] private Color _analysisStroke = InactiveStroke;

    [ObservableProperty] private bool _showHeaderTooltip = true;
    [ObservableProperty] private bool _showLatestReadingTooltip;
    [ObservableProperty] private bool _showAveragesTooltip;
    [ObservableProperty] private bool _showAnalysisTooltip;

    public OnboardingSampleDashboardViewModel()
    {
        ApplyStep();
    }

    [RelayCommand]
    public void NextStep()
    {
        if (CurrentStep >= _steps.Length - 1)
        {
            OnContinue?.Invoke();
            return;
        }

        CurrentStep++;
        ApplyStep();
    }

    [RelayCommand]
    public void SkipTour()
    {
        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }

    private void ApplyStep()
    {
        var step = _steps[CurrentStep];
        TourTitle = step.Title;
        TourDescription = step.Description;
        TourButtonText = step.ButtonText;
        StepIndicatorText = $"{CurrentStep + 1} of {_steps.Length}";

        HeaderOpacity = CurrentStep == 0 ? ActiveOpacity : DimmedOpacity;
        LatestReadingOpacity = CurrentStep == 1 ? ActiveOpacity : DimmedOpacity;
        AveragesOpacity = CurrentStep == 2 ? ActiveOpacity : DimmedOpacity;
        AnalysisOpacity = CurrentStep == 3 ? ActiveOpacity : DimmedOpacity;

        HeaderStroke = CurrentStep == 0 ? ActiveStroke : InactiveStroke;
        LatestReadingStroke = CurrentStep == 1 ? ActiveStroke : InactiveStroke;
        AveragesStroke = CurrentStep == 2 ? ActiveStroke : InactiveStroke;
        AnalysisStroke = CurrentStep == 3 ? ActiveStroke : InactiveStroke;

        ShowHeaderTooltip = CurrentStep == 0;
        ShowLatestReadingTooltip = CurrentStep == 1;
        ShowAveragesTooltip = CurrentStep == 2;
        ShowAnalysisTooltip = CurrentStep == 3;
    }
}
