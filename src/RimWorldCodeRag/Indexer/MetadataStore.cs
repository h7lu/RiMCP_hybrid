using System.Text.Json;

namespace RimWorldCodeRag.Indexer;

internal sealed class MetadataStore
{
    private readonly string _metadataFile;
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset> _mtimes = new();
    private bool _initialized;

    public MetadataStore(string metadataFile)
    {
        _metadataFile = metadataFile;
    }

    public void EnsureLoaded()
    {
        if (_initialized)
        {
            return;
        }

        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            if (File.Exists(_metadataFile))
            {
                var json = File.ReadAllText(_metadataFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? new();
                _mtimes = data.ToDictionary(kvp => kvp.Key, kvp => DateTimeOffset.FromUnixTimeMilliseconds(kvp.Value));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_metadataFile)!);
            }

            _initialized = true;
        }
    }

    public DateTimeOffset? GetTimestamp(string filePath)
    {
        EnsureLoaded();
        return _mtimes.TryGetValue(filePath, out var ts) ? ts : null;
    }

    public void SetTimestamp(string filePath, DateTimeOffset timestamp)
    {
        EnsureLoaded();
        lock (_gate)
        {
            _mtimes[filePath] = timestamp;
        }
    }

    public void Save()
    {
        EnsureLoaded();
        lock (_gate)
        {
            var data = _mtimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToUnixTimeMilliseconds());
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(_metadataFile, json);
        }
    }

    public IEnumerable<string> KnownPaths()
    {
        EnsureLoaded();
        return _mtimes.Keys;
    }
}
