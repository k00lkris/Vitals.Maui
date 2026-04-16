using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VisitDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private VisitLog? _original;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Doctor picker
    [ObservableProperty] private List<Doctor> _patientDoctors = new();
    [ObservableProperty] private Doctor? _selectedDoctor;

    // Visit fields
    [ObservableProperty] private DateTime _visitDate = DateTime.Now;
    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private DateTime _followUpDate = DateTime.Now.AddDays(7);
    [ObservableProperty] private bool _hasFollowUp;

    // Vitals snapshot
    [ObservableProperty] private string _systolic = string.Empty;
    [ObservableProperty] private string _diastolic = string.Empty;
    [ObservableProperty] private string _heartRate = string.Empty;
    [ObservableProperty] private string _oxygenSaturation = string.Empty;
    [ObservableProperty] private string _temperature = string.Empty;
    [ObservableProperty] private string _bloodGlucose = string.Empty;
    [ObservableProperty] private string _weight = string.Empty;

    public string PatientId { get; set; } = string.Empty;
    public Action? OnSaved { get; set; }

    public VisitDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public async Task InitializeAsync(VisitLog? visit, string patientId)
    {
        PatientId = patientId;
        IsAddMode = visit is null;
        IsEditing = IsAddMode;

        // Load patient's doctors for the picker
        var doctors = await _api.GetDoctorsAsync(patientId);
        PatientDoctors = doctors;

        if (visit is not null)
        {
            _original = visit;
            LoadFromVisit(visit);
        }
        else
        {
            ClearForm();
        }
    }

    private void LoadFromVisit(VisitLog visit)
    {
        VisitDate = string.IsNullOrEmpty(visit.VisitDate)
            ? DateTime.Now
            : DateTime.Parse(visit.VisitDate).ToLocalTime();

        Reason = visit.Reason ?? string.Empty;
        Notes = visit.Notes ?? string.Empty;
        HasFollowUp = !string.IsNullOrEmpty(visit.FollowUpDate);
        FollowUpDate = HasFollowUp
            ? DateTime.Parse(visit.FollowUpDate!)
            : DateTime.Now.AddDays(7);

        SelectedDoctor = PatientDoctors
            .FirstOrDefault(d => d.DoctorId == visit.DoctorId);

        // Vitals not stored on visit object — cleared
        ClearVitals();
    }

    private void ClearForm()
    {
        VisitDate = DateTime.Now;
        Reason = string.Empty;
        Notes = string.Empty;
        HasFollowUp = false;
        FollowUpDate = DateTime.Now.AddDays(7);
        SelectedDoctor = null;
        ClearVitals();
    }

    private void ClearVitals()
    {
        Systolic = string.Empty;
        Diastolic = string.Empty;
        HeartRate = string.Empty;
        OxygenSaturation = string.Empty;
        Temperature = string.Empty;
        BloodGlucose = string.Empty;
        Weight = string.Empty;
    }

    private int? TryParseInt(string val) =>
        int.TryParse(val, out var i) ? i : null;

    private double? TryParseDouble(string val) =>
        double.TryParse(val, out var d) ? d : null;

    [RelayCommand]
    public void StartEdit() => IsEditing = true;

    [RelayCommand]
    public void Cancel()
    {
        if (_original is not null)
            LoadFromVisit(_original);
        IsEditing = IsAddMode;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
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
                    doctor_id = SelectedDoctor?.DoctorId,
                    visit_date = VisitDate.ToUniversalTime().ToString("o"),
                    reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason,
                    notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                    follow_up_date = HasFollowUp
                        ? FollowUpDate.ToString("yyyy-MM-dd")
                        : null,
                    systolic = TryParseInt(Systolic),
                    diastolic = TryParseInt(Diastolic),
                    heart_rate = TryParseInt(HeartRate),
                    oxygen_saturation = TryParseInt(OxygenSaturation),
                    temperature = TryParseDouble(Temperature),
                    blood_glucose = TryParseInt(BloodGlucose),
                    weight = TryParseDouble(Weight)
                };
                success = await _api.CreateVisitAsync(payload);
            }
            else
            {
                var payload = new
                {
                    doctor_id = SelectedDoctor?.DoctorId,
                    visit_date = VisitDate.ToUniversalTime().ToString("o"),
                    reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason,
                    notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                    follow_up_date = HasFollowUp
                        ? FollowUpDate.ToString("yyyy-MM-dd")
                        : null,
                };
                success = await _api.UpdateVisitAsync(_original!.VisitId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode ? "Visit logged." : "Visit updated.";
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
}