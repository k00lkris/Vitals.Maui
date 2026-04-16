using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class GeneratePdfViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<PdfReport> _reports = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private bool _hasNoReports;

    // Day buttons
    [ObservableProperty] private int _selectedDays = 15;
    [ObservableProperty] private Color _btn15Color = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _btn30Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn45Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn60Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btnCustomColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private string _customDaysLabel = "Custom";

    private string ReportsFolder =>
        Path.Combine(FileSystem.AppDataDirectory, "VitalsReports");

    public GeneratePdfViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                LoadReports();
            }
        };
    }

    [RelayCommand]
    public Task LoadAsync()
    {
        LoadReports();
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task SelectDaysAsync(string days)
    {
        if (int.TryParse(days, out var d))
        {
            SelectedDays = d;
            UpdateButtonColors(d);
            CustomDaysLabel = "Custom";
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
        }
    }

    [RelayCommand]
    public async Task GeneratePdfAsync()
    {
        if (_patientState.SelectedPatient is null)
        {
            StatusMessage = "No patient selected.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        IsSuccess = false;

        try
        {
            var patient = _patientState.SelectedPatient;
            var bytes = await _api.GetPdfAsync(patient.PatientId, SelectedDays);

            if (bytes is null || bytes.Length == 0)
            {
                StatusMessage = "Could not generate PDF. Please try again.";
                return;
            }

            // Build filename
            var safeName = $"{patient.FirstName}_{patient.LastName}"
                .Replace(" ", "_");
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
            var fileName = $"Vitals_{safeName}_{SelectedDays}day_{timestamp}.pdf";

            // Ensure folder exists
            if (!Directory.Exists(ReportsFolder))
                Directory.CreateDirectory(ReportsFolder);

            var filePath = Path.Combine(ReportsFolder, fileName);
            await File.WriteAllBytesAsync(filePath, bytes);

            IsSuccess = true;
            StatusMessage = $"PDF generated successfully.";
            LoadReports();
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

    [RelayCommand]
    public async Task OpenReportAsync(PdfReport report)
    {
        try
        {
            var uri = new Uri(report.FilePath);
            await Launcher.OpenAsync(
                new OpenFileRequest
                {
                    File = new ReadOnlyFile(report.FilePath)
                });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open PDF: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ShareReportAsync(PdfReport report)
    {
        try
        {
            await Share.RequestAsync(new ShareFileRequest
            {
                Title = report.DisplayName,
                File = new ShareFile(report.FilePath)
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not share PDF: {ex.Message}";
        }
    }

    [RelayCommand]
    public void DeleteReport(PdfReport report)
    {
        try
        {
            if (File.Exists(report.FilePath))
                File.Delete(report.FilePath);
            LoadReports();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete PDF: {ex.Message}";
        }
    }

    private void LoadReports()
    {
        if (_patientState.SelectedPatient is null)
        {
            Reports = new ObservableCollection<PdfReport>();
            HasNoReports = true;
            return;
        }

        try
        {
            if (!Directory.Exists(ReportsFolder))
            {
                Reports = new ObservableCollection<PdfReport>();
                HasNoReports = true;
                return;
            }

            var patientName = $"{_patientState.SelectedPatient.FirstName}_{_patientState.SelectedPatient.LastName}";

            var files = Directory.GetFiles(ReportsFolder, "*.pdf")
                .Where(f => Path.GetFileName(f).Contains(patientName))
                .OrderByDescending(f => File.GetCreationTime(f))
                .Select(f =>
                {
                    var fileName = Path.GetFileName(f);
                    var parts = fileName.Replace(".pdf", "").Split('_');
                    var days = 15;
                    foreach (var part in parts)
                        if (part.EndsWith("day") && int.TryParse(part.Replace("day", ""), out var d))
                            days = d;

                    return new PdfReport
                    {
                        FileName = fileName,
                        FilePath = f,
                        GeneratedAt = File.GetCreationTime(f),
                        PatientName = _patientState.SelectedPatient.FullName,
                        Days = days
                    };
                })
                .ToList();

            Reports = new ObservableCollection<PdfReport>(files);
            HasNoReports = !Reports.Any();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== LOAD REPORTS ERROR: {ex.Message}");
            HasNoReports = true;
        }
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
}