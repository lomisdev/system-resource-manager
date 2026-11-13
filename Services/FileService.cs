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
        Root = Canonicalize(Path.GetFullPath(root));   // resolve Root with the same rules
        _trasher = trasher;
    }

    // Walk the path one component at a time, replacing any symlink with where it really points.
    private static string Canonicalize(string fullPath)
    {
        var current = "/";
        foreach (var part in fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null)              // null when not a link OR not existing
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    // followLeaf: true  -> we ENTER this path (list it, create inside it)
    // followLeaf: false -> we ACT ON this path itself (delete/trash: a link is removed, not its target)
    private string EnsureInsideRoot(string path, bool followLeaf)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var real = followLeaf || Path.GetDirectoryName(full) is not { } parent
            ? Canonicalize(full)
            : Path.Combine(Canonicalize(parent), Path.GetFileName(full));

        var rel = Path.GetRelativePath(Root, real);
        if (rel == ".." || rel.StartsWith("../") || Path.IsPathRooted(rel))
            throw new UnauthorizedAccessException("Path is outside the allowed folder.");
        return real;
    }

    public void MoveToTrash(string path)
    {
        var full = EnsureInsideRoot(path, followLeaf: false);  // act on the link itself, not its target
        if (full == Root) throw new InvalidOperationException("Refusing to trash the root folder.");
        if (_trasher is null) throw new InvalidOperationException("No trash backend configured.");
        _trasher.Trash(full);
    }

    public IReadOnlyList<FileEntry> List(string dir)
    {
        var full = EnsureInsideRoot(dir, followLeaf: true);
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
        var full = EnsureInsideRoot(path, followLeaf: false);
        if (full == Root) throw new InvalidOperationException("Refusing to delete the root folder.");

        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else File.Delete(full);
    }

    private string ResolveChild(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException("Invalid name.");

        var parent = EnsureInsideRoot(dir, followLeaf: true);                 // folder we create in: fully resolved
        return EnsureInsideRoot(Path.Combine(parent, name), followLeaf: false); // the new name doesn't exist yet
    }

}