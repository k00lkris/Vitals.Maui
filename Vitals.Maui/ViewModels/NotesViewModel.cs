using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class NotesViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<PatientNote> _notes = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoNotes;

    public NotesViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadNotesAsync();
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadNotesAsync();
    }

    public async Task LoadNotesAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var notes = await _api.GetNotesAsync(
                _patientState.SelectedPatient.PatientId);
            Notes = new ObservableCollection<PatientNote>(notes);
            HasNoNotes = !Notes.Any();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load notes: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenNoteDetailAsync(PatientNote note)
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<NoteDetailViewModel>()!;
        await vm.InitializeAsync(note, _patientState.SelectedPatient!.PatientId);

        var popup = new NoteDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadNotesAsync();
    }

    [RelayCommand]
    public async Task OpenAddNoteAsync()
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<NoteDetailViewModel>()!;
        await vm.InitializeAsync(null, _patientState.SelectedPatient!.PatientId);

        var popup = new NoteDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadNotesAsync();
    }
}