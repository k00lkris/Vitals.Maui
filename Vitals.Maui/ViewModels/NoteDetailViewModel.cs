using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class NoteDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private PatientNote? _original;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Note type buttons
    [ObservableProperty] private Color _generalColor = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _medicationChangeColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _behavioralColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _caregiverHandoffColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _familyCommunicationColor = Color.FromArgb("#0f3460");
    private string _selectedNoteType = "general";

    // Fields
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _selectedNoteTypeLabel = "General";
    [ObservableProperty] private string _bodyPlaceholder = "Write your note here...";

    public string PatientId { get; set; } = string.Empty;
    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }

    public bool ShowDeleteButton => IsEditing && !IsAddMode;

    partial void OnIsEditingChanged(bool value) =>
        OnPropertyChanged(nameof(ShowDeleteButton));

    partial void OnIsAddModeChanged(bool value) =>
        OnPropertyChanged(nameof(ShowDeleteButton));

    public NoteDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public Task InitializeAsync(PatientNote? note, string patientId)
    {
        PatientId = patientId;
        IsAddMode = note is null;
        IsEditing = IsAddMode;

        if (note is not null)
        {
            _original = note;
            LoadFromNote(note);
        }
        else
        {
            ClearForm();
            SelectNoteType("general");
        }

        return Task.CompletedTask;
    }

    private void LoadFromNote(PatientNote note)
    {
        Title = note.Title ?? string.Empty;
        Body = note.Body ?? string.Empty;
        SelectNoteType(note.NoteType);
    }

    private void ClearForm()
    {
        Title = string.Empty;
        Body = string.Empty;
    }

    // Note type selection commands
    [RelayCommand] public void SelectGeneral() => SelectNoteType("general");
    [RelayCommand] public void SelectMedicationChange() => SelectNoteType("medication_change");
    [RelayCommand] public void SelectBehavioral() => SelectNoteType("behavioral_observation");
    [RelayCommand] public void SelectCaregiverHandoff() => SelectNoteType("caregiver_handoff");
    [RelayCommand] public void SelectFamilyCommunication() => SelectNoteType("family_communication");

    private void SelectNoteType(string type)
    {
        _selectedNoteType = type;
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");

        GeneralColor = type == "general" ? active : inactive;
        MedicationChangeColor = type == "medication_change" ? active : inactive;
        BehavioralColor = type == "behavioral_observation" ? active : inactive;
        CaregiverHandoffColor = type == "caregiver_handoff" ? active : inactive;
        FamilyCommunicationColor = type == "family_communication" ? active : inactive;

        SelectedNoteTypeLabel = type switch
        {
            "general" => "General",
            "medication_change" => "Medication Change",
            "behavioral_observation" => "Behavioral Observation",
            "caregiver_handoff" => "Caregiver Handoff",
            "family_communication" => "Family Communication",
            _ => "General"
        };

        BodyPlaceholder = type switch
        {
            "general" => "Write your note here...",
            "medication_change" => "Describe the medication change and reason...",
            "behavioral_observation" => "Describe the behavior observed, time, duration, triggers...",
            "caregiver_handoff" => "Summarize current status, medications given, upcoming needs...",
            "family_communication" => "Summarize the communication, who was involved, decisions made...",
            _ => "Write your note here..."
        };
    }

    [RelayCommand]
    public void StartEdit() => IsEditing = true;

    [RelayCommand]
    public void Cancel()
    {
        if (_original is not null)
            LoadFromNote(_original);
        IsEditing = IsAddMode;
        if (IsAddMode)
            OnCancelled?.Invoke();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            StatusMessage = "Note body cannot be empty.";
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
                    note_type = _selectedNoteType,
                    title = string.IsNullOrWhiteSpace(Title) ? null : Title,
                    body = Body
                };
                success = await _api.CreateNoteAsync(payload);
            }
            else
            {
                var payload = new
                {
                    note_type = _selectedNoteType,
                    title = string.IsNullOrWhiteSpace(Title) ? null : Title,
                    body = Body
                };
                success = await _api.UpdateNoteAsync(_original!.NoteId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode ? "Note saved." : "Note updated.";
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
            "Delete Note",
            "Are you sure you want to delete this note?",
            "Delete", "Cancel");

        if (!confirm) return;

        IsBusy = true;
        try
        {
            var payload = new { is_active = false };
            var success = await _api.UpdateNoteAsync(_original!.NoteId, payload);
            if (success)
                OnSaved?.Invoke();
            else
                StatusMessage = "Could not delete note.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}