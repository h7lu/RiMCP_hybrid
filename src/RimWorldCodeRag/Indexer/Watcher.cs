using System;
using System.Collections.Generic;
using System.IO;

namespace RimWorldCodeRag.Indexer;

internal sealed class SourceWatcher : IDisposable
{
    private readonly string _root;
    private FileSystemWatcher? _csWatcher;
    private FileSystemWatcher? _xmlWatcher;

    public event EventHandler<IReadOnlyCollection<string>>? FilesChanged;

    public SourceWatcher(string root)
    {
        _root = root;
    }

    public void Start()
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Source root '{_root}' does not exist.");
        }

        _csWatcher = CreateWatcher("*.cs");
        _xmlWatcher = CreateWatcher("*.xml");
    }

    private FileSystemWatcher CreateWatcher(string filter)
    {
        var watcher = new FileSystemWatcher(_root, filter)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };
        watcher.Changed += HandleChanged;
        watcher.Created += HandleChanged;
        watcher.Renamed += HandleRenamed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void HandleChanged(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        FilesChanged?.Invoke(this, new[] { path });
    }

    private void HandleRenamed(object sender, RenamedEventArgs e)
    {
        FilesChanged?.Invoke(this, new[] { e.OldFullPath, e.FullPath });
    }

    public void Dispose()
    {
        if (_csWatcher != null)
        {
            _csWatcher.Dispose();
        }
        if (_xmlWatcher != null)
        {
            _xmlWatcher.Dispose();
        }
    }
}
