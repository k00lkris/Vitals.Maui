using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.Drawing;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class IncidentDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private IncidentLog? _original;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Fields
    [ObservableProperty] private DateTime _incidentDate = DateTime.Now;
    [ObservableProperty] private string _incidentType = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _outcome = string.Empty;
    [ObservableProperty] private bool _followUpNeeded;
    [ObservableProperty] private string _followUpNotes = string.Empty;

    // Severity buttons
    [ObservableProperty] private Color _lowColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _mediumColor = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _highColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _criticalColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private string _selectedSeverityLabel = "Medium";
    private string _selectedSeverity = "medium";

    public string PatientId { get; set; } = string.Empty;
    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }
    public bool ShowDeleteButton => IsEditing && !IsAddMode;

    partial void OnIsEditingChanged(bool value) =>
        OnPropertyChanged(nameof(ShowDeleteButton));

    partial void OnIsAddModeChanged(bool value) =>
        OnPropertyChanged(nameof(ShowDeleteButton));
    public IncidentDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public Task InitializeAsync(IncidentLog? incident, string patientId)
    {
        PatientId = patientId;
        IsAddMode = incident is null;
        IsEditing = IsAddMode;

        if (incident is not null)
        {
            _original = incident;
            LoadFromIncident(incident);
        }
        else
        {
            ClearForm();
            SelectSeverity("medium");
        }

        return Task.CompletedTask;
    }

    private void LoadFromIncident(IncidentLog incident)
    {
        IncidentDate = string.IsNullOrEmpty(incident.IncidentDate)
            ? DateTime.Now
            : DateTime.Parse(incident.IncidentDate).ToLocalTime();

        IncidentType = incident.IncidentType ?? string.Empty;
        Location = incident.Location ?? string.Empty;
        Description = incident.Description ?? string.Empty;
        Outcome = incident.Outcome ?? string.Empty;
        FollowUpNeeded = incident.FollowUpNeeded;
        FollowUpNotes = incident.FollowUpNotes ?? string.Empty;
        SelectSeverity(incident.Severity ?? "medium");
    }

    private void ClearForm()
    {
        IncidentDate = DateTime.Now;
        IncidentType = string.Empty;
        Location = string.Empty;
        Description = string.Empty;
        Outcome = string.Empty;
        FollowUpNeeded = false;
        FollowUpNotes = string.Empty;
    }

    [RelayCommand] public void SelectLow() => SelectSeverity("low");
    [RelayCommand] public void SelectMedium() => SelectSeverity("medium");
    [RelayCommand] public void SelectHigh() => SelectSeverity("high");
    [RelayCommand] public void SelectCritical() => SelectSeverity("critical");

    private void SelectSeverity(string severity)
    {
        _selectedSeverity = severity;
        LowColor = severity == "low"
            ? Color.FromArgb("#388e3c") : Color.FromArgb("#0f3460");
        MediumColor = severity == "medium"
            ? Color.FromArgb("#1976d2") : Color.FromArgb("#0f3460");
        HighColor = severity == "high"
            ? Color.FromArgb("#d32f2f") : Color.FromArgb("#0f3460");
        CriticalColor = severity == "critical"
            ? Color.FromArgb("#7b1fa2") : Color.FromArgb("#0f3460");
        SelectedSeverityLabel = severity switch
        {
            "low" => "Low",
            "medium" => "Medium",
            "high" => "High",
            "critical" => "Critical",
            _ => "Medium"
        };
    }

    [RelayCommand]
    public void StartEdit() => IsEditing = true;

    [RelayCommand]
    public void Cancel()
    {
        if (_original is not null)
            LoadFromIncident(_original);
        IsEditing = IsAddMode;
        if (IsAddMode)
            OnCancelled?.Invoke();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Description) &&
            string.IsNullOrWhiteSpace(IncidentType))
        {
            StatusMessage = "Please enter an incident type or description.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            bool success;

            if (IsAddMode)
            {
                var payload = new
                {
                    patient_id = PatientId,
                    incident_date = IncidentDate.ToUniversalTime().ToString("o"),
                    severity = _selectedSeverity,
                    incident_type = string.IsNullOrWhiteSpace(IncidentType)
                        ? null : IncidentType,
                    location = string.IsNullOrWhiteSpace(Location)
                        ? null : Location,
                    description = string.IsNullOrWhiteSpace(Description)
                        ? null : Description,
                    outcome = string.IsNullOrWhiteSpace(Outcome)
                        ? null : Outcome,
                    follow_up_needed = FollowUpNeeded,
                    follow_up_notes = string.IsNullOrWhiteSpace(FollowUpNotes)
                        ? null : FollowUpNotes
                };
                success = await _api.CreateIncidentAsync(payload);
            }
            else
            {
                var payload = new
                {
                    incident_date = IncidentDate.ToUniversalTime().ToString("o"),
                    severity = _selectedSeverity,
                    incident_type = string.IsNullOrWhiteSpace(IncidentType)
                        ? null : IncidentType,
                    location = string.IsNullOrWhiteSpace(Location)
                        ? null : Location,
                    description = string.IsNullOrWhiteSpace(Description)
                        ? null : Description,
                    outcome = string.IsNullOrWhiteSpace(Outcome)
                        ? null : Outcome,
                    follow_up_needed = FollowUpNeeded,
                    follow_up_notes = string.IsNullOrWhiteSpace(FollowUpNotes)
                        ? null : FollowUpNotes
                };
                success = await _api.UpdateIncidentAsync(
                    _original!.IncidentId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode
                    ? "Incident logged." : "Incident updated.";
                OnSaved?.Invoke();
            }
            else
            {
                StatusMessage = "Something went wrong. Please try again.";
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

    [RelayCommand]
    public async Task DeleteAsync()
    {
        var confirm = await Shell.Current.DisplayAlert(
            "Delete Incident",
            "Are you sure you want to delete this incident log?",
            "Delete", "Cancel");

        if (!confirm) return;

        IsBusy = true;
        try
        {
            var payload = new { is_active = false };
            var success = await _api.UpdateIncidentAsync(
                _original!.IncidentId, payload);
            if (success)
                OnSaved?.Invoke();
            else
                StatusMessage = "Could not delete incident.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}