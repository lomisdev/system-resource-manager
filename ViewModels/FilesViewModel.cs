// ViewModels/FilesViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System_Resource_Manager.Services;
using System.Threading.Tasks;

namespace System_Resource_Manager.ViewModels;

public partial class FilesViewModel : ViewModelBase
{
    private readonly FileService _fs =
    new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), new GioTrasher());

    [ObservableProperty] private string _currentDir = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isConfirmingDelete;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(AskDeleteCommand), nameof(TrashCommand), nameof(EditCommand))]
    private FileEntry? _selected;

    public ObservableCollection<FileEntry> Entries { get; } = new();

    public FilesViewModel()
    {
        CurrentDir = _fs.Root;
        Refresh();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrashCommand), nameof(AskDeleteCommand))]
    private bool _isBusy;

    partial void OnSelectedChanged(FileEntry? value) => IsConfirmingDelete = false;

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            var items = _fs.List(CurrentDir);
            Entries.Clear();
            foreach (var e in items) Entries.Add(e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    private bool CanOpen() => Selected?.IsDirectory == true;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() { CurrentDir = Selected!.FullPath; Refresh(); }

    [RelayCommand]
    private void Up()
    {
        if (CurrentDir == _fs.Root) return;
        CurrentDir = Path.GetDirectoryName(CurrentDir) ?? _fs.Root;
        Refresh();
    }

    [RelayCommand] private Task CreateFileAsync() => RunAsync(() => _fs.CreateFile(CurrentDir, NewName), $"Created file '{NewName}'");
    [RelayCommand] private Task CreateFolderAsync() => RunAsync(() => _fs.CreateFolder(CurrentDir, NewName), $"Created folder '{NewName}'");

    private bool CanDelete() => Selected is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task TrashAsync()
    {
        if (Selected is null) return;
        var target = Selected;
        await RunAsync(() => _fs.MoveToTrash(target.FullPath), $"Moved '{target.Name}' to Trash");
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void AskDelete() => IsConfirmingDelete = true;

    [RelayCommand] private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (Selected is null) return;
        var target = Selected;
        IsConfirmingDelete = false;
        await RunAsync(() => _fs.Delete(target.FullPath), $"Deleted '{target.Name}'");
    }

    // ---- editor ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(EditorTitle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _editorText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(EditorTitle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    private string _editorPath = "";

    [ObservableProperty] private bool _isConfirmingDiscard;

    private string _loadedText = "";     // what is on disk, as far as we know
    private FileStamp? _stamp;           // file identity at load/last save, for conflict detection

    public bool IsDirty => IsEditing && EditorText != _loadedText;
    public string EditorTitle => $"{Path.GetFileName(EditorPath)}{(IsDirty ? "  ●" : "")}";

    private void RefreshDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditorTitle));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => Selected is { IsDirectory: false };

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAsync()
    {
        if (Selected is not { IsDirectory: false } target) return;   // capture BEFORE awaiting
        Status = "Opening…";
        try
        {
            var (text, stamp) = await Task.Run(() => _fs.ReadText(target.FullPath));
            _loadedText = text;          // set before EditorText so it never looks dirty
            _stamp = stamp;
            EditorPath = target.FullPath;
            EditorText = text;
            IsEditing = true;
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    private bool CanSave() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_stamp is null) return;
        // Snapshot first: the user may keep typing while the save runs.
        var path = EditorPath;
        var text = EditorText;
        var stamp = _stamp;
        try
        {
            _stamp = await Task.Run(() => _fs.WriteText(path, text, stamp));
            _loadedText = text;          // the snapshot, so keystrokes made during the save stay "dirty"
            Status = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = ex.Message;
        }
        RefreshDirty();
    }

    [RelayCommand]
    private void CloseEditor()
    {
        if (IsDirty) { IsConfirmingDiscard = true; return; }
        LeaveEditor();
    }

    [RelayCommand] private void DiscardChanges() => LeaveEditor();
    [RelayCommand] private void KeepEditing() => IsConfirmingDiscard = false;

    private void LeaveEditor()
    {
        IsConfirmingDiscard = false;
        IsEditing = false;
        EditorText = "";
        EditorPath = "";
        _loadedText = "";
        _stamp = null;
        Refresh();                       // sizes and dates in the list may have changed
    }

    private async Task RunAsync(Action action, string success)
    {
        IsBusy = true;
        Status = "Working…";
        try
        {
            await Task.Run(action);          // file work on a worker thread
            Status = success;                // back on the UI thread here
            NewName = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
        }
        finally { IsBusy = false; }
        Refresh();
    }
}