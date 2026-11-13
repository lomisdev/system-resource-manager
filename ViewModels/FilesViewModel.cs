// ViewModels/FilesViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System_Resource_Manager.Services;

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
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(AskDeleteCommand), nameof(TrashCommand))]
    private FileEntry? _selected;

    public ObservableCollection<FileEntry> Entries { get; } = new();

    public FilesViewModel()
    {
        CurrentDir = _fs.Root;
        Refresh();
    }

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

    [RelayCommand] private void CreateFile() => Run(() => _fs.CreateFile(CurrentDir, NewName), $"Created file '{NewName}'");
    [RelayCommand] private void CreateFolder() => Run(() => _fs.CreateFolder(CurrentDir, NewName), $"Created folder '{NewName}'");

    private bool CanDelete() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Trash()
    {
        if (Selected is null) return;
        var target = Selected;
        Run(() => _fs.MoveToTrash(target.FullPath), $"Moved '{target.Name}' to Trash");
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void AskDelete() => IsConfirmingDelete = true;

    [RelayCommand] private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (Selected is null) return;
        var target = Selected;
        IsConfirmingDelete = false;
        Run(() => _fs.Delete(target.FullPath), $"Deleted '{target.Name}'");
    }

    private void Run(Action action, string success)
    {
        try { action(); Status = success; NewName = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
        }
        Refresh();
    }
}