using System.Collections.Concurrent;

namespace RimWorldCodeRag.Retrieval;

//LRU缓存，用于存储查询的embedding，省一点算力。简单来说就是个队列缓存
internal sealed class EmbeddingCache
{
    private readonly int _maxSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lock = new();

    public EmbeddingCache(int maxSize = 100)
    {
        _maxSize = Math.Max(1, maxSize);
    }

    public bool TryGet(string query, out float[] embedding)
    {
        if (_cache.TryGetValue(query, out var entry))
        {
            lock (_lock)
            {
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
            }
            embedding = entry.Embedding;
            return true;
        }

        embedding = Array.Empty<float>();
        return false;
    }

    public void Add(string query, float[] embedding)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(query, out var existing))
            {
                _lruList.Remove(existing.Node);
                _lruList.AddFirst(existing.Node);
                _cache[query] = new CacheEntry(embedding, existing.Node);
                return;
            }

            if (_cache.Count >= _maxSize)
            {
                var oldest = _lruList.Last;
                if (oldest != null)
                {
                    _lruList.RemoveLast();
                    _cache.TryRemove(oldest.Value, out _);
                }
            }

            var node = _lruList.AddFirst(query);
            _cache[query] = new CacheEntry(embedding, node);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
    }

    private readonly record struct CacheEntry(float[] Embedding, LinkedListNode<string> Node);
}
