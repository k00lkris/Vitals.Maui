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

    // Day buttons
    [ObservableProperty] private int _selectedDays = 30;
    [ObservableProperty] private Color _btn15Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn30Color = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _btn45Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn60Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btnCustomColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private string _customDaysLabel = "Custom";

    // Plain English summary
    [ObservableProperty] private string _plainEnglishSummary = string.Empty;
    [ObservableProperty] private string _pcpLine = string.Empty;

    // Tab selection
    [ObservableProperty] private Color _tabBpColor = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _tabHrColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _tabSpo2Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _tabTempColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private bool _showBp = true;
    [ObservableProperty] private bool _showHr = false;
    [ObservableProperty] private bool _showSpo2 = false;
    [ObservableProperty] private bool _showTemp = false;

    // Secondary plain English
    [ObservableProperty] private string _hrSummary = string.Empty;
    [ObservableProperty] private string _spo2Summary = string.Empty;
    [ObservableProperty] private string _tempSummary = string.Empty;

    public VitalsAnalysisViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;
    }

    public async Task LoadAsync(int days = 30)
    {
        SelectedDays = days;
        UpdateButtonColors(days);
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

    private void BuildPlainEnglish(VitalsAnalysis a)
    {
        if (a.Systolic is null) return;

        var sys = a.Systolic;
        var parts = new List<string>();

        // Hypotension override
        if (a.Classification == "hypotension")
        {
            PlainEnglishSummary =
                $"Your average blood pressure of {a.Systolic.Avg:F1}/{a.Diastolic?.Avg:F1} mmHg is below normal range. " +
                "Low blood pressure can cause dizziness, fatigue, and fainting. " +
                "If you are experiencing symptoms, contact your care team.";
            return;
        }

        // Borderline hypotension override
        if (a.Classification == "borderline_hypotension")
        {
            var hypoBelow = a.HypoBurden is not null
                ? $" About {a.HypoBurden.ModeratePct + a.HypoBurden.SeverePct:F0}% of readings fell below 90 mmHg" +
                  (a.HypoBurden.SeverePct >= 5
                      ? $", including {a.HypoBurden.SeverePct:F0}% in the severe range below 80 mmHg."
                      : ".")
                : string.Empty;

            PlainEnglishSummary =
                $"Your average blood pressure of {a.Systolic.Avg:F1}/{a.Diastolic?.Avg:F1} mmHg is at the lower end of the normal range. " +
                $"While not critically low, this pattern is worth monitoring — especially for symptoms like lightheadedness or dizziness on standing.{hypoBelow} " +
                "Recording at consistent times and noting any symptoms will help your care team assess this pattern.";
            return;
        }

        // Trend statement
        parts.Add(sys.Trend switch
        {
            "rising_significant" =>
                $"Your systolic blood pressure has been rising at a statistically significant rate ({sys.SlopeDisplay}) over the past {SelectedDays} days.",
            "rising" =>
                $"Your systolic blood pressure has been gradually rising ({sys.SlopeDisplay}) over the past {SelectedDays} days, though the trend is not yet statistically significant.",
            "falling_significant" =>
                $"Your systolic blood pressure has been falling at a statistically significant rate ({sys.SlopeDisplay}) over the past {SelectedDays} days — this is a positive sign.",
            "falling" =>
                $"Your systolic blood pressure has been gradually improving ({sys.SlopeDisplay}) over the past {SelectedDays} days.",
            "stable" =>
                $"Your systolic blood pressure has been stable over the past {SelectedDays} days.",
            _ => string.Empty
        });

        // Momentum statement
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
                "accelerating" => "Your improvement is continuing to accelerate.",
                "decelerating" => "Your improvement is slowing down.",
                _ => string.Empty
            });
        }

        // Consistency statement
        parts.Add(sys.Consistency switch
        {
            "high" => "Your readings are very consistent, which improves the reliability of this analysis.",
            "moderate" => "Your readings show moderate variability. Try to record at the same time each day for a more accurate picture.",
            "low" => "Your readings are quite variable. Recording at the same time each day will help identify clearer trends.",
            _ => string.Empty
        });

        // Burden statement
        if (a.Burden is not null)
        {
            if (a.Burden.Stage2Pct > 20)
                parts.Add($"About {a.Burden.Stage2Pct:F0}% of your readings fall in the Stage 2 range (≥140 mmHg). This is worth discussing with your doctor.");
            else if (a.Burden.Stage1Pct > 30)
                parts.Add($"About {a.Burden.Stage1Pct:F0}% of your readings fall in the Stage 1 range (130–139 mmHg).");
            else if (a.Burden.NormalPct > 70)
                parts.Add($"{a.Burden.NormalPct:F0}% of your readings are in the normal range — great work.");
        }

        PlainEnglishSummary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void BuildPcpLine(VitalsAnalysis a)
    {
        if (string.IsNullOrEmpty(a.PcpName))
        {
            PcpLine = string.Empty;
            return;
        }

        if (!string.IsNullOrEmpty(a.NextFollowup))
            PcpLine = a.Classification switch
            {
                "hypotension" =>
                    $"Please discuss your low blood pressure readings with {a.PcpName} at your appointment on {a.NextFollowup}.",
                "borderline_hypotension" =>
                    $"Consider mentioning your borderline-low blood pressure readings to {a.PcpName} at your appointment on {a.NextFollowup}.",
                _ =>
                    $"Consider sharing this summary with {a.PcpName} at your next appointment on {a.NextFollowup}."
            };
        else
            PcpLine = a.Classification switch
            {
                "hypotension" =>
                    $"Please discuss your low blood pressure readings with {a.PcpName} at your next visit.",
                "borderline_hypotension" =>
                    $"Consider mentioning your borderline-low blood pressure readings to {a.PcpName} at your next visit.",
                _ =>
                    $"You may want to discuss these results with {a.PcpName} during your next visit."
            };
    }

    private void UpdateButtonColors(int days)
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        Btn15Color = days == 15 ? active : inactive;
        Btn30Color = days == 30 ? active : inactive;
        Btn45Color = days == 45 ? active : inactive;
        Btn60Color = days == 60 ? active : inactive;
        BtnCustomColor = days == -1 ? active : inactive;
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");

        ShowBp = tab == "bp";
        ShowHr = tab == "hr";
        ShowSpo2 = tab == "spo2";
        ShowTemp = tab == "temp";

        TabBpColor = tab == "bp" ? active : inactive;
        TabHrColor = tab == "hr" ? active : inactive;
        TabSpo2Color = tab == "spo2" ? active : inactive;
        TabTempColor = tab == "temp" ? active : inactive;
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
}