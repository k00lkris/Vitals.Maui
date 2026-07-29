using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsAnalysisViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private VitalsAnalysis? _analysis;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isInsufficient;
    [ObservableProperty] private bool _isOk;

    // Day buttons — background colors
    [ObservableProperty] private int _selectedDays = 30;

    private Color _btn15Color = Colors.Transparent;
    public Color Btn15Color { get => _btn15Color; set { _btn15Color = value; OnPropertyChanged(); } }

    private Color _btn30Color = Colors.Transparent;
    public Color Btn30Color { get => _btn30Color; set { _btn30Color = value; OnPropertyChanged(); } }

    private Color _btn45Color = Colors.Transparent;
    public Color Btn45Color { get => _btn45Color; set { _btn45Color = value; OnPropertyChanged(); } }

    private Color _btn60Color = Colors.Transparent;
    public Color Btn60Color { get => _btn60Color; set { _btn60Color = value; OnPropertyChanged(); } }

    private Color _btnCustomColor = Colors.Transparent;
    public Color BtnCustomColor { get => _btnCustomColor; set { _btnCustomColor = value; OnPropertyChanged(); } }

    // Day buttons — text colors
    private Color _btn15TextColor = Colors.White;
    public Color Btn15TextColor { get => _btn15TextColor; set { _btn15TextColor = value; OnPropertyChanged(); } }

    private Color _btn30TextColor = Colors.White;
    public Color Btn30TextColor { get => _btn30TextColor; set { _btn30TextColor = value; OnPropertyChanged(); } }

    private Color _btn45TextColor = Colors.White;
    public Color Btn45TextColor { get => _btn45TextColor; set { _btn45TextColor = value; OnPropertyChanged(); } }

    private Color _btn60TextColor = Colors.White;
    public Color Btn60TextColor { get => _btn60TextColor; set { _btn60TextColor = value; OnPropertyChanged(); } }

    private Color _btnCustomTextColor = Colors.White;
    public Color BtnCustomTextColor { get => _btnCustomTextColor; set { _btnCustomTextColor = value; OnPropertyChanged(); } }

    [ObservableProperty] private string _customDaysLabel = "Custom";

    // Plain English summary
    [ObservableProperty] private string _plainEnglishSummary = string.Empty;
    [ObservableProperty] private string _pcpLine = string.Empty;

    // Tab buttons — background colors
    private Color _tabBpColor = Colors.Transparent;
    public Color TabBpColor { get => _tabBpColor; set { _tabBpColor = value; OnPropertyChanged(); } }

    private Color _tabHrColor = Colors.Transparent;
    public Color TabHrColor { get => _tabHrColor; set { _tabHrColor = value; OnPropertyChanged(); } }

    private Color _tabSpo2Color = Colors.Transparent;
    public Color TabSpo2Color { get => _tabSpo2Color; set { _tabSpo2Color = value; OnPropertyChanged(); } }

    private Color _tabTempColor = Colors.Transparent;
    public Color TabTempColor { get => _tabTempColor; set { _tabTempColor = value; OnPropertyChanged(); } }

    // Tab buttons — text colors
    private Color _tabBpTextColor = Colors.White;
    public Color TabBpTextColor { get => _tabBpTextColor; set { _tabBpTextColor = value; OnPropertyChanged(); } }

    private Color _tabHrTextColor = Colors.White;
    public Color TabHrTextColor { get => _tabHrTextColor; set { _tabHrTextColor = value; OnPropertyChanged(); } }

    private Color _tabSpo2TextColor = Colors.White;
    public Color TabSpo2TextColor { get => _tabSpo2TextColor; set { _tabSpo2TextColor = value; OnPropertyChanged(); } }

    private Color _tabTempTextColor = Colors.White;
    public Color TabTempTextColor { get => _tabTempTextColor; set { _tabTempTextColor = value; OnPropertyChanged(); } }

    [ObservableProperty] private bool _showBp = true;
    [ObservableProperty] private bool _showHr = false;
    [ObservableProperty] private bool _showSpo2 = false;
    [ObservableProperty] private bool _showTemp = false;

    // Secondary plain English
    [ObservableProperty] private string _hrSummary = string.Empty;
    [ObservableProperty] private string _spo2Summary = string.Empty;
    [ObservableProperty] private string _tempSummary = string.Empty;

    [ObservableProperty] private bool _showDiastolicWarning = false;
    [ObservableProperty] private string _diastolicWarningText = string.Empty;

    public VitalsAnalysisViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;
    }

    public async Task LoadAsync(int days = 30)
    {
        SelectedDays = days;
        UpdateButtonColors(days);
        SelectTab("bp");
        await RunAnalysisAsync();
    }

    [RelayCommand]
    public async Task SelectDaysAsync(string days)
    {
        if (int.TryParse(days, out var d))
        {
            SelectedDays = d;
            UpdateButtonColors(d);
            CustomDaysLabel = "Custom";
            await RunAnalysisAsync();
        }
    }

    [RelayCommand]
    public async Task SelectCustomDaysAsync(string customDays)
    {
        if (int.TryParse(customDays, out var d) && d > 0)
        {
            SelectedDays = d;
            CustomDaysLabel = $"{d}d";
            UpdateButtonColors(-1);
            await RunAnalysisAsync();
        }
    }

    private async Task RunAnalysisAsync()
    {
        SelectTab("bp");
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _api.GetVitalsAnalysisAsync(
                _patientState.SelectedPatient.PatientId, SelectedDays);

            Analysis = result;

            if (result is null)
            {
                StatusMessage = "Could not load analysis. Check your connection.";
                IsInsufficient = false;
                IsOk = false;
                return;
            }

            IsInsufficient = result.IsInsufficient;
            IsOk = result.IsOk;

            if (result.IsOk)
            {
                BuildPlainEnglish(result);
                BuildPcpLine(result);
                BuildHrSummary(result);
                BuildSpo2Summary(result);
                BuildTempSummary(result);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateButtonColors(int days)
    {
        if (Application.Current?.Resources is null) return;

        var res = Application.Current.Resources;
        var active = res.TryGetValue("ButtonBackground", out var a) ? (Color)a : Color.FromArgb("#00acc1");
        var inactive = res.TryGetValue("ButtonSecondary", out var i) ? (Color)i : Color.FromArgb("#b2dff2");
        var activeTxt = res.TryGetValue("TextPrimary", out var at) ? (Color)at : Colors.White;
        var inactiveTxt = res.TryGetValue("ButtonSecondaryText", out var it) ? (Color)it : Color.FromArgb("#0d2137");

        Btn15Color = days == 15 ? active : inactive;
        Btn30Color = days == 30 ? active : inactive;
        Btn45Color = days == 45 ? active : inactive;
        Btn60Color = days == 60 ? active : inactive;
        BtnCustomColor = days == -1 ? active : inactive;

        Btn15TextColor = days == 15 ? activeTxt : inactiveTxt;
        Btn30TextColor = days == 30 ? activeTxt : inactiveTxt;
        Btn45TextColor = days == 45 ? activeTxt : inactiveTxt;
        Btn60TextColor = days == 60 ? activeTxt : inactiveTxt;
        BtnCustomTextColor = days == -1 ? activeTxt : inactiveTxt;
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        if (Application.Current?.Resources is null) return;

        var res = Application.Current.Resources;
        var active = res.TryGetValue("ButtonBackground", out var a) ? (Color)a : Color.FromArgb("#00acc1");
        var inactive = res.TryGetValue("ButtonSecondary", out var i) ? (Color)i : Color.FromArgb("#b2dff2");
        var activeTxt = res.TryGetValue("TextPrimary", out var at) ? (Color)at : Colors.White;
        var inactiveTxt = res.TryGetValue("ButtonSecondaryText", out var it) ? (Color)it : Color.FromArgb("#0d2137");

        ShowBp = tab == "bp";
        ShowHr = tab == "hr";
        ShowSpo2 = tab == "spo2";
        ShowTemp = tab == "temp";

        TabBpColor = tab == "bp" ? active : inactive;
        TabHrColor = tab == "hr" ? active : inactive;
        TabSpo2Color = tab == "spo2" ? active : inactive;
        TabTempColor = tab == "temp" ? active : inactive;

        TabBpTextColor = tab == "bp" ? activeTxt : inactiveTxt;
        TabHrTextColor = tab == "hr" ? activeTxt : inactiveTxt;
        TabSpo2TextColor = tab == "spo2" ? activeTxt : inactiveTxt;
        TabTempTextColor = tab == "temp" ? activeTxt : inactiveTxt;
    }

    private void BuildPlainEnglish(VitalsAnalysis a)
    {
        if (a.Systolic is null) return;

        var sys = a.Systolic;

        ShowDiastolicWarning = false;
        DiastolicWarningText = string.Empty;

        if (a.Classification == "borderline_hypotension" &&
            a.Diastolic is not null &&
            a.Diastolic.Significant &&
            a.Diastolic.Slope > 0.2 &&
            !sys.Significant)
        {
            ShowDiastolicWarning = true;
            var momentumNote = a.Diastolic.Momentum == "accelerating"
                ? " The rate of increase is accelerating."
                : string.Empty;
            DiastolicWarningText =
                $"⚠️ While pumping pressure appears stable, resting pressure between beats " +
                $"has been rising at a statistically significant rate " +
                $"({a.Diastolic.SlopeDisplay} mmHg/day).{momentumNote} " +
                $"This pattern warrants discussion with her care team.";
        }

        if (a.Classification == "hypotension")
        {
            PlainEnglishSummary =
                $"Average blood pressure of {a.Systolic.Avg:F1}/{a.Diastolic?.Avg:F1} mmHg " +
                $"is below normal range. Low blood pressure can cause dizziness, fatigue, " +
                $"and fainting. If experiencing symptoms, contact the care team.";
            BuildBurdenSummary(a);
            return;
        }

        if (a.Classification == "borderline_hypotension")
        {
            var hypoBelow = a.HypoBurden is not null
                ? $" About {a.HypoBurden.ModeratePct + a.HypoBurden.SeverePct:F0}% of readings " +
                  $"fell below 90 mmHg" +
                  (a.HypoBurden.SeverePct >= 5
                      ? $", including {a.HypoBurden.SeverePct:F0}% in the severe range below 80 mmHg."
                      : ".")
                : string.Empty;

            PlainEnglishSummary =
                $"Average blood pressure of {a.Systolic.Avg:F1}/{a.Diastolic?.Avg:F1} mmHg " +
                $"is at the lower end of the normal range. While not critically low, this " +
                $"pattern is worth monitoring — especially for symptoms like lightheadedness " +
                $"or dizziness on standing.{hypoBelow} " +
                $"Recording at consistent times and noting any symptoms will help the care " +
                $"team assess this pattern.";
            BuildBurdenSummary(a);
            return;
        }

        var parts = new List<string>();

        parts.Add(sys.Trend switch
        {
            "rising_significant" =>
                $"Pumping pressure has been rising at a statistically significant rate " +
                $"({sys.SlopeDisplay}) over the past {SelectedDays} days.",
            "rising" =>
                $"Pumping pressure has been gradually rising ({sys.SlopeDisplay}) over " +
                $"the past {SelectedDays} days, though the trend is not yet statistically significant.",
            "falling_significant" =>
                $"Pumping pressure has been falling at a statistically significant rate " +
                $"({sys.SlopeDisplay}) over the past {SelectedDays} days — a positive sign.",
            "falling" =>
                $"Pumping pressure has been gradually improving ({sys.SlopeDisplay}) " +
                $"over the past {SelectedDays} days.",
            "stable" =>
                $"Pumping pressure has been stable over the past {SelectedDays} days.",
            _ => string.Empty
        });

        if (sys.Trend is "rising_significant" or "rising")
        {
            parts.Add(sys.Momentum switch
            {
                "accelerating" => "The rate of increase is accelerating.",
                "decelerating" => "The rate of increase appears to be slowing down.",
                _ => string.Empty
            });
        }
        else if (sys.Trend is "falling_significant" or "falling")
        {
            parts.Add(sys.Momentum switch
            {
                "accelerating" => "The improvement is continuing to accelerate.",
                "decelerating" => "The improvement is slowing down.",
                _ => string.Empty
            });
        }

        parts.Add(sys.Consistency switch
        {
            "high" => "Readings are very consistent, which improves the reliability of this analysis.",
            "moderate" => "Readings show moderate variability. Recording at the same time each day gives a more accurate picture.",
            "low" => "Readings are quite variable. Recording at the same time each day will help identify clearer trends.",
            _ => string.Empty
        });

        if (a.Diastolic is not null && a.Diastolic.Significant && a.Diastolic.Slope > 0.2)
        {
            var diaExtra = a.Diastolic.Momentum == "accelerating"
                ? " The rate of increase is accelerating."
                : a.Diastolic.Momentum == "decelerating"
                    ? " The rate of increase appears to be slowing down."
                    : string.Empty;
            parts.Add(
                $"Notably, resting pressure between beats has been rising at a statistically " +
                $"significant rate ({a.Diastolic.SlopeDisplay} mmHg/day) — worth monitoring " +
                $"even if pumping pressure appears stable.{diaExtra}");
        }
        else if (a.Diastolic is not null && a.Diastolic.Significant && a.Diastolic.Slope < -0.2)
        {
            parts.Add(
                $"Resting pressure between beats has been falling at a statistically " +
                $"significant rate ({a.Diastolic.SlopeDisplay} mmHg/day) — a positive sign.");
        }

        PlainEnglishSummary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        BuildBurdenSummary(a);
    }

    private void BuildPcpLine(VitalsAnalysis a)
    {
        if (string.IsNullOrEmpty(a.PcpName))
        {
            PcpLine = string.Empty;
            return;
        }

        bool diastolicRising = a.Diastolic is not null &&
                               a.Diastolic.Significant &&
                               a.Diastolic.Slope > 0.2;

        if (!string.IsNullOrEmpty(a.NextFollowup))
            PcpLine = a.Classification switch
            {
                "hypotension" =>
                    $"Please discuss the low blood pressure readings with {a.PcpName} " +
                    $"at the appointment on {a.NextFollowup}.",
                "borderline_hypotension" when diastolicRising =>
                    $"Consider discussing the borderline-low pumping pressure and the " +
                    $"rising resting pressure trend with {a.PcpName} at the appointment " +
                    $"on {a.NextFollowup}.",
                "borderline_hypotension" =>
                    $"Consider mentioning the borderline-low blood pressure readings to " +
                    $"{a.PcpName} at the appointment on {a.NextFollowup}.",
                _ =>
                    $"Consider sharing this summary with {a.PcpName} at the next " +
                    $"appointment on {a.NextFollowup}."
            };
        else
            PcpLine = a.Classification switch
            {
                "hypotension" =>
                    $"Please discuss the low blood pressure readings with {a.PcpName} " +
                    $"at the next visit.",
                "borderline_hypotension" when diastolicRising =>
                    $"Consider discussing the borderline-low pumping pressure and the " +
                    $"rising resting pressure trend with {a.PcpName} at the next visit.",
                "borderline_hypotension" =>
                    $"Consider mentioning the borderline-low blood pressure readings to " +
                    $"{a.PcpName} at the next visit.",
                _ =>
                    $"You may want to discuss these results with {a.PcpName} during " +
                    $"the next visit."
            };
    }

    private void BuildHrSummary(VitalsAnalysis a)
    {
        if (a.HeartRate is null) { HrSummary = string.Empty; return; }
        var hr = a.HeartRate;
        var parts = new List<string>();

        parts.Add(hr.Classification switch
        {
            "bradycardia" =>
                $"Average heart rate of {hr.Avg:F0} BPM is below the normal range (60–100 BPM). " +
                "A resting rate below 60 BPM can be normal for athletes but may indicate bradycardia in others.",
            "mild_tachycardia" =>
                $"Average heart rate of {hr.Avg:F0} BPM is mildly elevated. " +
                "A consistently elevated resting rate can be caused by stress, dehydration, or underlying conditions.",
            "tachycardia" =>
                $"Average heart rate of {hr.Avg:F0} BPM is above normal range. " +
                "A persistently elevated heart rate warrants medical evaluation.",
            _ =>
                $"Average heart rate of {hr.Avg:F0} BPM is within the normal range of 60–100 BPM."
        });

        parts.Add(hr.Trend switch
        {
            "rising_significant" => $"Heart rate has been rising at a statistically significant rate ({hr.SlopeDisplay} BPM/day).",
            "rising" => $"Heart rate has been gradually rising ({hr.SlopeDisplay} BPM/day).",
            "falling_significant" => $"Heart rate has been falling at a statistically significant rate ({hr.SlopeDisplay} BPM/day).",
            "falling" => $"Heart rate has been gradually declining ({hr.SlopeDisplay} BPM/day).",
            _ => "Heart rate has been stable over this period."
        });

        var burden = hr.HrBurden;
        if (burden is not null)
        {
            if (burden.BradycardiaPct >= 20)
                parts.Add($"About {burden.BradycardiaPct:F0}% of readings were below 60 BPM (bradycardia range).");
            else if (burden.TachycardiaPct >= 10)
                parts.Add($"About {burden.TachycardiaPct:F0}% of readings exceeded 120 BPM (tachycardia range).");
            else if (burden.MildTachyPct >= 20)
                parts.Add($"About {burden.MildTachyPct:F0}% of readings were mildly elevated (101–120 BPM).");
            else if (burden.NormalPct >= 80)
                parts.Add($"{burden.NormalPct:F0}% of readings were in the normal range — consistent cardiac rhythm.");
        }

        HrSummary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void BuildSpo2Summary(VitalsAnalysis a)
    {
        if (a.Spo2 is null) { Spo2Summary = string.Empty; return; }
        var spo2 = a.Spo2;
        var parts = new List<string>();

        parts.Add(spo2.Classification switch
        {
            "mild_hypoxemia" =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is mildly below normal. " +
                "Values between 92–94% may warrant supplemental oxygen evaluation.",
            "moderate_hypoxemia" =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is moderately low. " +
                "This range is associated with significant breathing difficulty and should be evaluated promptly.",
            "severe_hypoxemia" =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is critically low. " +
                "Readings below 88% require immediate medical attention.",
            _ =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is within the normal range (≥95%)."
        });

        parts.Add(spo2.Trend switch
        {
            "rising_significant" => "Oxygenation has been improving significantly — a positive sign.",
            "rising" => "Oxygenation has been gradually improving.",
            "falling_significant" => "Oxygenation has been declining significantly. This warrants medical attention.",
            "falling" => "Oxygenation has been gradually declining.",
            _ => "Oxygenation has been stable over this period."
        });

        var burden = spo2.Spo2Burden;
        if (burden is not null)
        {
            if (burden.SevereHypoxemiaPct >= 5)
                parts.Add($"About {burden.SevereHypoxemiaPct:F0}% of readings were critically low (below 88%) — this requires clinical review.");
            else if (burden.ModerateHypoxemiaPct >= 10)
                parts.Add($"About {burden.ModerateHypoxemiaPct:F0}% of readings were in the moderate hypoxemia range (88–91%).");
            else if (burden.MildHypoxemiaPct >= 15)
                parts.Add($"About {burden.MildHypoxemiaPct:F0}% of readings were mildly low (92–94%).");
            else if (burden.NormalPct >= 80)
                parts.Add($"{burden.NormalPct:F0}% of readings were in the normal range (≥95%).");
        }

        Spo2Summary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void BuildTempSummary(VitalsAnalysis a)
    {
        if (a.Temperature is null) { TempSummary = string.Empty; return; }
        var temp = a.Temperature;
        var parts = new List<string>();

        parts.Add(temp.Classification switch
        {
            "hypothermia" =>
                $"Average temperature of {temp.Avg:F1}°F is below normal range. " +
                "Temperatures below 96.8°F can indicate hypothermia and should be evaluated.",
            "slightly_elevated" =>
                $"Average temperature of {temp.Avg:F1}°F is slightly elevated. " +
                "This may indicate early illness or mild inflammation.",
            "fever" =>
                $"Average temperature of {temp.Avg:F1}°F indicates a fever. " +
                "Persistent fever above 100.4°F should be evaluated by your care team.",
            "high_fever" =>
                $"Average temperature of {temp.Avg:F1}°F indicates a high fever. " +
                "Temperatures above 103°F require prompt medical attention.",
            _ =>
                $"Average temperature of {temp.Avg:F1}°F is within the normal range (96.8–98.9°F)."
        });

        parts.Add(temp.Trend switch
        {
            "rising_significant" => "Temperature has been rising significantly over this period.",
            "rising" => "Temperature has been gradually rising.",
            "falling_significant" => "Temperature has been falling significantly.",
            "falling" => "Temperature has been gradually falling.",
            _ => "Temperature has been stable over this period."
        });

        var burden = temp.TempBurden;
        if (burden is not null)
        {
            if (burden.HighFeverPct >= 5)
                parts.Add($"About {burden.HighFeverPct:F0}% of readings were above 103°F (high fever range).");
            else if (burden.FeverPct >= 10)
                parts.Add($"About {burden.FeverPct:F0}% of readings were in the fever range (100.4–103°F).");
            else if (burden.ElevatedPct >= 15)
                parts.Add($"About {burden.ElevatedPct:F0}% of readings were slightly elevated (99–100.3°F).");
            else if (burden.HypothermiaPct >= 10)
                parts.Add($"About {burden.HypothermiaPct:F0}% of readings were below 96.8°F (hypothermia range).");
            else if (burden.NormalPct >= 80)
                parts.Add($"{burden.NormalPct:F0}% of readings were in the normal range — temperature is well controlled.");
        }

        TempSummary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void BuildBurdenSummary(VitalsAnalysis a)
    {
        if (a.Map is not null)
        {
            if (!string.IsNullOrWhiteSpace(PlainEnglishSummary))
                PlainEnglishSummary += $" {a.Map.PlainEnglish}";
            else
                PlainEnglishSummary = a.Map.PlainEnglish;
        }
    }
}