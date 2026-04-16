using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class MedicationDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private Medication? _original;

    // Mode
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Fields
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _dosage = string.Empty;
    [ObservableProperty] private string _purpose = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _isActive = true;

    // Time of day toggles
    [ObservableProperty] private bool _morning;
    [ObservableProperty] private bool _midday;
    [ObservableProperty] private bool _evening;
    [ObservableProperty] private bool _night;

    // Toggle colors
    [ObservableProperty] private Color _morningColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _middayColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _eveningColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _nightColor = Color.FromArgb("#0f3460");

    // Rx/OTC
    [ObservableProperty] private bool _isRx = true;
    [ObservableProperty] private Color _rxColor = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _otcColor = Color.FromArgb("#0f3460");

    // Refill info
    [ObservableProperty] private string _qty = string.Empty;
    [ObservableProperty] private string _daysSupply = string.Empty;
    [ObservableProperty] private string _fillDate = string.Empty;
    [ObservableProperty] private string _estRefill = string.Empty;

    // Doctors
    [ObservableProperty] private ObservableCollection<Doctor> _doctors = new();
    [ObservableProperty] private Doctor? _selectedDoctor;

    // Patient context
    public string PatientId { get; set; } = string.Empty;

    // Callback to refresh list after save
    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }

    public MedicationDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public async Task InitializeAsync(Medication? medication, string patientId)
    {
        PatientId = patientId;
        IsAddMode = medication is null;
        IsEditing = IsAddMode;

        // Load doctors
        var doctorList = await _api.GetDoctorsAsync(patientId);
        Doctors = new ObservableCollection<Doctor>(doctorList);

        if (medication is not null)
        {
            _original = medication;
            LoadFromMedication(medication);
        }
    }

    private void LoadFromMedication(Medication med)
    {
        Name = med.Name;
        Dosage = med.Dosage ?? string.Empty;
        Purpose = med.Purpose ?? string.Empty;
        Notes = string.Empty;
        IsActive = med.IsActive;
        Qty = med.Qty?.ToString() ?? string.Empty;
        DaysSupply = med.DaysSupply?.ToString() ?? string.Empty;
        FillDate = med.FillDate ?? string.Empty;
        EstRefill = med.EstRefill ?? string.Empty;
        IsRx = (med.RxOtc ?? "rx") == "rx";

        Morning = med.TimeOfDay.Contains("morning");
        Midday = med.TimeOfDay.Contains("midday");
        Evening = med.TimeOfDay.Contains("evening");
        Night = med.TimeOfDay.Contains("night");

        UpdateTimeOfDayColors();
        UpdateRxOtcColors();

        SelectedDoctor = Doctors.FirstOrDefault(d =>
            d.Name == med.PrescribingDoctor);


    }

    private void UpdateRxOtcColors()
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        RxColor = IsRx ? active : inactive;
        OtcColor = IsRx ? inactive : active;
    }


    [RelayCommand]
    public void StartEdit()
    {
        IsEditing = true;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_original is not null)
            LoadFromMedication(_original);
        IsEditing = IsAddMode;
        OnCancelled?.Invoke();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Medication name is required.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var timeOfDay = new List<string>();
            if (Morning) timeOfDay.Add("morning");
            if (Midday) timeOfDay.Add("midday");
            if (Evening) timeOfDay.Add("evening");
            if (Night) timeOfDay.Add("night");

            bool success;

            if (IsAddMode)
            {
                var payload = new
                {
                    patient_id = PatientId,
                    name = Name,
                    dosage = Dosage,
                    purpose = Purpose,
                    time_of_day = timeOfDay,
                    prescribing_doctor_id = SelectedDoctor?.DoctorId,
                    qty = string.IsNullOrEmpty(Qty) ? (int?)null : int.Parse(Qty),
                    days_supply = string.IsNullOrEmpty(DaysSupply) ? (int?)null : int.Parse(DaysSupply),
                    is_active = IsActive,
                    rxotc = IsRx ? "rx" : "otc"
                };
                success = await _api.AddMedicationAsync(payload);
            }
            else
            {
                var payload = new
                {
                    name = Name,
                    dosage = Dosage,
                    purpose = Purpose,
                    time_of_day = timeOfDay,
                    prescribing_doctor_id = SelectedDoctor?.DoctorId,
                    qty = string.IsNullOrEmpty(Qty) ? (int?)null : int.Parse(Qty),
                    days_supply = string.IsNullOrEmpty(DaysSupply) ? (int?)null : int.Parse(DaysSupply),
                    is_active = IsActive,
                    rxotc = IsRx ? "rx" : "otc"
                };
                success = await _api.UpdateMedicationAsync(
                    _original!.MedicationId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode ? "Medication added." : "Medication updated.";
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
    public void ToggleMorning()
    {
        Morning = !Morning;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void ToggleMidday()
    {
        Midday = !Midday;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void ToggleEvening()
    {
        Evening = !Evening;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void ToggleNight()
    {
        Night = !Night;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void SelectRx()
    {
        IsRx = true;
        UpdateRxOtcColors();
    }

    [RelayCommand]
    public void SelectOtc()
    {
        IsRx = false;
        UpdateRxOtcColors();
    }

    private void UpdateTimeOfDayColors()
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        MorningColor = Morning ? active : inactive;
        MiddayColor = Midday ? active : inactive;
        EveningColor = Evening ? active : inactive;
        NightColor = Night ? active : inactive;
    }
}