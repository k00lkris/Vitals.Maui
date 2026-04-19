using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Xml.Linq;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using static Microsoft.Maui.ApplicationModel.Permissions;

namespace Vitals.Maui.ViewModels;

public partial class DoctorDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private Doctor? _original;
    public bool ShowBackButton => CanGoBackToExisting && ShowNewDoctorForm;
    public bool ShowFormButtons => ShowNewDoctorForm;
    public bool ShowBottomBar => !ShowExistingDoctors;
    public bool ShowRemoveButton => IsEditing && !IsAddMode;


    // Mode
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Fields
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _specialty = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _fax = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _isActive = true;

    // Add to existing properties
    [ObservableProperty] private List<Doctor> _householdDoctors = new();
    [ObservableProperty] private bool _showExistingDoctors;
    [ObservableProperty] private bool _showNewDoctorForm;
    [ObservableProperty] private Doctor? _selectedExistingDoctor;
    [ObservableProperty] private bool _canGoBackToExisting;
    [ObservableProperty] private Doctor? _selectedHouseholdDoctor;
    [ObservableProperty] private LatestVisit? _latestVisit;
    [ObservableProperty] private bool _hasVisitHistory;

    // Patient context
    public string PatientId { get; set; } = string.Empty;

    // Callbacks
    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }

    partial void OnShowNewDoctorFormChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowFormButtons));
    }

    partial void OnCanGoBackToExistingChanged(bool value) =>
        OnPropertyChanged(nameof(ShowBackButton));

    partial void OnShowExistingDoctorsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBottomBar));
        OnPropertyChanged(nameof(ShowFormButtons));
    }
    partial void OnIsEditingChanged(bool value) =>
    OnPropertyChanged(nameof(ShowRemoveButton));

    partial void OnIsAddModeChanged(bool value) =>
        OnPropertyChanged(nameof(ShowRemoveButton));

    public DoctorDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public async Task InitializeAsync(Doctor? doctor, string patientId)
    {
        PatientId = patientId;
        IsAddMode = doctor is null;
        IsEditing = IsAddMode;

        if (doctor is not null)
        {
            _original = doctor;
            LoadFromDoctor(doctor);
            ShowExistingDoctors = false;
            ShowNewDoctorForm = true;

            System.Diagnostics.Debug.WriteLine($"=== LATEST VISIT: patientId={patientId} doctorId={doctor.DoctorId}");
            var latest = await _api.GetLatestVisitAsync(patientId, doctor.DoctorId);
            System.Diagnostics.Debug.WriteLine($"=== LATEST VISIT RESULT: {latest?.DisplayVisitDate ?? "null"}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LatestVisit = latest;
                HasVisitHistory = latest is not null;
            });
        }
        else
        {
            try
            {
                var household = await _api.GetHouseholdDoctorsAsync();

                List<Doctor> linked = new();
                if (!string.IsNullOrEmpty(patientId))
                    linked = await _api.GetDoctorsAsync(patientId);

                var linkedIds = linked
                    .Where(d => d?.DoctorId != null)
                    .Select(d => d.DoctorId)
                    .ToHashSet();

                HouseholdDoctors = household
                    .Where(d => d?.DoctorId != null && !linkedIds.Contains(d.DoctorId))
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== INIT DOCTORS ERROR: {ex.Message}");
                HouseholdDoctors = new List<Doctor>();
            }

            ShowExistingDoctors = HouseholdDoctors.Any();
            ShowNewDoctorForm = !HouseholdDoctors.Any();
            CanGoBackToExisting = HouseholdDoctors.Any();
            ClearForm();
        }
    }


    private void LoadFromDoctor(Doctor doc)
    {
        Name = doc.Name;
        Specialty = doc.Specialty ?? string.Empty;
        Phone = doc.Phone ?? string.Empty;
        Fax = doc.Fax ?? string.Empty;
        Email = doc.Email ?? string.Empty;
        Address = doc.Address ?? string.Empty;
        Notes = doc.Notes ?? string.Empty;
        IsPrimary = doc.IsPrimary;
        IsActive = doc.IsActive;
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
            LoadFromDoctor(_original);
        IsEditing = IsAddMode;
        OnCancelled?.Invoke();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Doctor name is required.";
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
                    name = Name,
                    specialty = Specialty,
                    phone = Phone,
                    fax = Fax,
                    email = Email,
                    address = Address,
                    notes = Notes,
                    is_primary = IsPrimary,
                    is_active = IsActive
                };
                success = await _api.AddDoctorAsync(payload);
            }
            else
            {
                var payload = new
                {
                    patient_id = PatientId,
                    name = Name,
                    specialty = Specialty,
                    phone = Phone,
                    fax = Fax,
                    email = Email,
                    address = Address,
                    notes = Notes,
                    is_primary = IsPrimary,
                    is_active = IsActive
                };
                success = await _api.UpdateDoctorAsync(
                    _original!.DoctorId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode ? "Provider added." : "Provider updated.";
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

    // Phone/Email actions
    [RelayCommand]
    public async Task CallPhoneAsync()
    {
        if (!string.IsNullOrWhiteSpace(Phone))
        {
            try
            {
                var uri = new Uri($"tel:{Phone.Replace(" ", "").Replace("-", "")}");
                await Launcher.OpenAsync(uri);
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task SendEmailAsync()
    {
        if (!string.IsNullOrWhiteSpace(Email))
        {
            try
            {
                var uri = new Uri($"mailto:{Email}");
                await Launcher.OpenAsync(uri);
            }
            catch { }
        }
    }

    [RelayCommand]
    public void ShowNewForm()
    {
        ShowExistingDoctors = false;
        ShowNewDoctorForm = true;
        // CanGoBackToExisting stays true if there were household doctors
        ClearForm();
    }

    [RelayCommand]
    public void BackToExisting()
    {
        ShowExistingDoctors = HouseholdDoctors.Any();
        ShowNewDoctorForm = false;
    }

    private void ClearForm()
    {
        Name = string.Empty;
        Specialty = string.Empty;
        Phone = string.Empty;
        Fax = string.Empty;
        Email = string.Empty;
        Address = string.Empty;
        Notes = string.Empty;
        IsPrimary = false;
    }

    [RelayCommand]
    public async Task LinkSelectedDoctorAsync()
    {
        if (SelectedHouseholdDoctor is null)
        {
            StatusMessage = "Please select a doctor first.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var success = await _api.LinkDoctorToPatientAsync(
                PatientId, SelectedHouseholdDoctor.DoctorId, IsPrimary);
            if (success)
            {
                StatusMessage = $"{SelectedHouseholdDoctor.Name} added to care team.";
                OnSaved?.Invoke();
            }
            else
            {
                StatusMessage = "Could not link doctor. Please try again.";
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
    public async Task RemoveFromCareTeamAsync()
    {
        var confirm = await Shell.Current.DisplayAlert(
            "Remove Doctor",
            $"Remove {Name} from this patient's care team? The doctor record will be kept and can be re-added later.",
            "Remove",
            "Cancel");

        if (!confirm) return;

        IsBusy = true;
        try
        {
            var success = await _api.UnlinkDoctorFromPatientAsync(
                PatientId, _original!.DoctorId);
            if (success)
            {
                StatusMessage = $"{Name} removed from care team.";
                OnSaved?.Invoke();
            }
            else
            {
                StatusMessage = "Could not remove doctor. Please try again.";
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