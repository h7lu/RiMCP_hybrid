using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RimWorldCodeRag.Retrieval;

internal sealed class VectorIndex
{
    private readonly List<VectorIndexEntry> _entries;
    private readonly Dictionary<string, VectorIndexEntry> _byId;

    private VectorIndex(List<VectorIndexEntry> entries)
    {
        _entries = entries;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, VectorIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Id))
            {
                continue;
            }
            _byId[entry.Id] = entry;
        }
    }

    public static VectorIndex Load(string directory)
    {
        var path = Path.Combine(directory, "vectors.jsonl");
        if (!File.Exists(path))
        {
            return new VectorIndex(new List<VectorIndexEntry>());
        }

        var entries = new List<VectorIndexEntry>();
    using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idProp))
                {
                    continue;
                }

                var id = idProp.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var pathValue = root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;
                var signatureValue = root.TryGetProperty("signature", out var sigProp) ? sigProp.GetString() : null;
                var previewValue = root.TryGetProperty("preview", out var previewProp) ? previewProp.GetString() ?? string.Empty : string.Empty;

                var identifiers = Array.Empty<string>();
                if (root.TryGetProperty("identifiers", out var identifiersProp) && identifiersProp.ValueKind == JsonValueKind.Array)
                {
                    identifiers = identifiersProp
                        .EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }

                var vector = Array.Empty<float>();
                if (root.TryGetProperty("vector", out var vectorProp) && vectorProp.ValueKind == JsonValueKind.Array)
                {
                    vector = vectorProp
                        .EnumerateArray()
                        .Select(e => e.ValueKind switch
                        {
                            JsonValueKind.Number when e.TryGetSingle(out var f) => f,
                            JsonValueKind.Number when e.TryGetDouble(out var d) => (float)d,
                            _ => 0f
                        })
                        .ToArray();
                }

                if (!seen.Add(id))
                {
                    continue;
                }

                entries.Add(new VectorIndexEntry
                {
                    Id = id!,
                    Path = pathValue,
                    Signature = signatureValue,
                    Preview = previewValue,
                    Identifiers = identifiers,
                    Vector = vector
                });
            }
            catch
            {
                // Skip malformed entries but continue loading the remainder of the index.
            }
        }

        return new VectorIndex(entries);
    }

    public IReadOnlyList<VectorIndexEntry> Entries => _entries;

    public int VectorDimensions => _entries.Count == 0 ? 0 : _entries[0].Vector.Length;

    public VectorIndexEntry? GetById(string id)
    {
        return _byId.TryGetValue(id, out var entry) ? entry : null;
    }

    public IReadOnlyList<VectorMatch> FindNearest(float[] queryVector, int k)
    {
        if (queryVector.Length == 0 || _entries.Count == 0)
        {
            return Array.Empty<VectorMatch>();
        }

        var matches = new List<VectorMatch>(_entries.Count);
        foreach (var entry in _entries)
        {
            if (entry.Vector.Length != queryVector.Length)
            {
                continue;
            }

            var score = DotProduct(queryVector, entry.Vector);
            matches.Add(new VectorMatch(entry, score));
        }

        matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (matches.Count > k)
        {
            matches.RemoveRange(k, matches.Count - k);
        }

        return matches;
    }

    private static float DotProduct(float[] a, IReadOnlyList<float> b)
    {
        if (a.Length != b.Count)
        {
            return 0f;
        }

#if NET6_0_OR_GREATER
        // Use SIMD-accelerated operations when available
        if (b is float[] bArray)
        {
            return System.Numerics.Tensors.TensorPrimitives.Dot(a, bArray);
        }
#endif

        // Fallback to manual computation
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return (float)sum;
    }
}

internal sealed record VectorIndexEntry
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string? Signature { get; init; }
    public required string Preview { get; init; }
    public required string[] Identifiers { get; init; }
    public required float[] Vector { get; init; }
}

internal readonly record struct VectorMatch(VectorIndexEntry Entry, float Score);