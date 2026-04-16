using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class AllergyDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private Allergy? _original;

    // Mode
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Fields
    [ObservableProperty] private string _allergen = string.Empty;
    [ObservableProperty] private string _reaction = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _isActive = true;

    // Allergy Type toggles
    [ObservableProperty] private Color _medicationColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _foodColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _environmentalColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _otherTypeColor = Color.FromArgb("#0f3460");
    private string _selectedType = "medication";

    // Severity toggles
    [ObservableProperty] private Color _mildColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _moderateColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _severeColor = Color.FromArgb("#0f3460");
    private string _selectedSeverity = "mild";

    // Display labels
    [ObservableProperty] private string _selectedTypeLabel = "Medication";
    [ObservableProperty] private string _selectedSeverityLabel = "Mild";

    public string PatientId { get; set; } = string.Empty;
    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }

    public AllergyDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public Task InitializeAsync(Allergy? allergy, string patientId)
    {
        PatientId = patientId;
        IsAddMode = allergy is null;
        IsEditing = IsAddMode;

        if (allergy is not null)
        {
            _original = allergy;
            LoadFromAllergy(allergy);
        }
        else
        {
            // Defaults for add mode
            SelectType("medication");
            SelectSeverity("mild");
        }

        return Task.CompletedTask;
    }

    private void LoadFromAllergy(Allergy allergy)
    {
        Allergen = allergy.Allergen;
        Reaction = allergy.Reaction ?? string.Empty;
        Notes = allergy.Notes ?? string.Empty;
        IsActive = allergy.IsActive;
        SelectType(allergy.AllergyType);
        SelectSeverity(allergy.Severity ?? "mild");
    }

    [RelayCommand]
    public void StartEdit() => IsEditing = true;

    [RelayCommand]
    public void Cancel()
    {
        if (_original is not null)
            LoadFromAllergy(_original);
        IsEditing = IsAddMode;
        OnCancelled?.Invoke();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Allergen))
        {
            StatusMessage = "Allergen name is required.";
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
                    allergen = Allergen,
                    allergy_type = _selectedType,
                    reaction = string.IsNullOrWhiteSpace(Reaction) ? null : Reaction,
                    severity = _selectedSeverity,
                    notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                    is_active = IsActive
                };
                success = await _api.AddAllergyAsync(payload);
            }
            else
            {
                var payload = new
                {
                    allergen = Allergen,
                    allergy_type = _selectedType,
                    reaction = string.IsNullOrWhiteSpace(Reaction) ? null : Reaction,
                    severity = _selectedSeverity,
                    notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                    is_active = IsActive
                };
                success = await _api.UpdateAllergyAsync(_original!.AllergyId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode ? "Allergy added." : "Allergy updated.";
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

    // Type selection
    [RelayCommand] public void SelectMedication() => SelectType("medication");
    [RelayCommand] public void SelectFood() => SelectType("food");
    [RelayCommand] public void SelectEnvironmental() => SelectType("environmental");
    [RelayCommand] public void SelectOtherType() => SelectType("other");

    private void SelectType(string type)
    {
        _selectedType = type;
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        MedicationColor = type == "medication" ? active : inactive;
        FoodColor = type == "food" ? active : inactive;
        EnvironmentalColor = type == "environmental" ? active : inactive;
        OtherTypeColor = type == "other" ? active : inactive;
        SelectedTypeLabel = type switch
        {
            "medication" => "Medication",
            "food" => "Food",
            "environmental" => "Environmental",
            "other" => "Other",
            _ => "Medication"
        };
    }

    // Severity selection
    [RelayCommand] public void SelectMild() => SelectSeverity("mild");
    [RelayCommand] public void SelectModerate() => SelectSeverity("moderate");
    [RelayCommand] public void SelectSevere() => SelectSeverity("severe");

    private void SelectSeverity(string severity)
    {
        _selectedSeverity = severity;
        MildColor = severity == "mild" ? Color.FromArgb("#388e3c") : Color.FromArgb("#0f3460");
        ModerateColor = severity == "moderate" ? Color.FromArgb("#f57c00") : Color.FromArgb("#0f3460");
        SevereColor = severity == "severe" ? Color.FromArgb("#d32f2f") : Color.FromArgb("#0f3460");
        SelectedSeverityLabel = severity switch
        {
            "mild" => "Mild",
            "moderate" => "Moderate",
            "severe" => "Severe",
            _ => "Mild"
        };
    }
}