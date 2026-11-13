// Services/FileService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace System_Resource_Manager.Services;

public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long SizeBytes, DateTime Modified)
{
    public string DisplayName => IsDirectory ? Name + "/" : Name;
    public string SizeText => IsDirectory ? "" : SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / 1048576.0:F1} MB"
    };
}

public sealed class FileService
{
    public string Root { get; }

    private readonly ITrasher? _trasher;

    public FileService(string root, ITrasher? trasher = null)
    {
        Root = Path.GetFullPath(root);
        _trasher = trasher;
    }

    public void MoveToTrash(string path)
    {
        var full = EnsureInsideRoot(path);   // same sandbox check as Delete
        if (full == Root) throw new InvalidOperationException("Refusing to trash the root folder.");
        if (_trasher is null) throw new InvalidOperationException("No trash backend configured.");
        _trasher.Trash(full);
    }

    public IReadOnlyList<FileEntry> List(string dir)
    {
        var full = EnsureInsideRoot(dir);
        return new DirectoryInfo(full).EnumerateFileSystemInfos()
            .Select(i => i is FileInfo f
                ? new FileEntry(f.Name, f.FullName, false, f.Length, f.LastWriteTime)
                : new FileEntry(i.Name, i.FullName, true, 0, i.LastWriteTime))
            .OrderByDescending(e => e.IsDirectory)           // folders first
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CreateFile(string dir, string name)
    {
        var path = ResolveChild(dir, name);
        // CreateNew throws IOException if the file exists. File.Create would TRUNCATE it.
        using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    }

    public void CreateFolder(string dir, string name)
    {
        var path = ResolveChild(dir, name);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"'{name}' already exists.");
        Directory.CreateDirectory(path);
    }

    public void Delete(string path)
    {
        var full = EnsureInsideRoot(path);
        if (full == Root) throw new InvalidOperationException("Refusing to delete the root folder.");

        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else File.Delete(full);
    }

    private string ResolveChild(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException("Invalid name.");
        return EnsureInsideRoot(Path.Combine(EnsureInsideRoot(dir), name));
    }

    // Normalise (collapses ".." segments), then require the result to be Root or below it.
    private string EnsureInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(Root, full);
        if (rel == ".." || rel.StartsWith("../") || Path.IsPathRooted(rel))
            throw new UnauthorizedAccessException("Path is outside the allowed folder.");
        return full;
    }
}