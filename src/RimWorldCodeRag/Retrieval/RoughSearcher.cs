using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Indexer;

namespace RimWorldCodeRag.Retrieval;

public sealed class RoughSearcher : IDisposable
{
    private const float IdentifierBoost = 2.5f;
    private const float MixedBoost = 1.0f;
    private const float SemanticWeight = 2.0f;

    private readonly RoughSearchConfig _config;
    private readonly FSDirectory _directory;
    private readonly DirectoryReader _reader;
    private readonly IndexSearcher _searcher;
    private readonly StandardAnalyzer _analyzer;
    private readonly QueryParser _symbolIdParser;
    private readonly QueryParser _textParser;
    private readonly VectorIndex _vectorIndex;
    private readonly IQueryEmbeddingGenerator _queryEmbeddingGenerator;

    public RoughSearcher(RoughSearchConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();

        _directory = FSDirectory.Open(_config.LuceneIndexPath);
        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader);
        _analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
        
        _symbolIdParser = new QueryParser(LuceneVersion.LUCENE_48, LuceneWriter.FieldSymbolId, _analyzer)
        {
            DefaultOperator = Operator.OR
        };

        _textParser = new QueryParser(LuceneVersion.LUCENE_48, LuceneWriter.FieldText, _analyzer)
        {
            DefaultOperator = Operator.OR
        };

        _vectorIndex = VectorIndex.Load(_config.VectorIndexPath);
        _queryEmbeddingGenerator = CreateQueryEmbeddingGenerator();
    }

    public async Task<IReadOnlyList<RoughSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<RoughSearchResult>();
        }

        // Stage 1: Lexical pre-filter to reduce search space
        var lexicalTask = Task.Run(() => SearchLexical(query, _config.LexicalCandidates), cancellationToken);
        
        await lexicalTask.ConfigureAwait(false);
        var lexicalMatches = lexicalTask.Result;

        // Stage 2: Semantic ranking on lexical candidates
        IReadOnlyList<VectorMatch> semanticMatches;
        if (_vectorIndex.Entries.Count == 0 || lexicalMatches.Count == 0)
        {
            semanticMatches = Array.Empty<VectorMatch>();
        }
        else
        {
            // Build candidate set from lexical results
            var candidateIds = new HashSet<string>(lexicalMatches.Select(m => m.SymbolId), StringComparer.OrdinalIgnoreCase);
            
            var queryVector = await _queryEmbeddingGenerator.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
            
            // Filter vector entries to only lexical candidates
            var filteredEntries = _vectorIndex.Entries.Where(e => candidateIds.Contains(e.Id)).ToList();
            
            var matches = new List<VectorMatch>(filteredEntries.Count);
            foreach (var entry in filteredEntries)
            {
                if (entry.Vector.Length != queryVector.Length)
                {
                    continue;
                }
                var score = DotProduct(queryVector, entry.Vector);
                matches.Add(new VectorMatch(entry, score));
            }
            
            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            var semanticTake = Math.Max(_config.MaxResults, _config.SemanticCandidates);
            semanticMatches = matches.Take(semanticTake).ToList();
        }

        Console.WriteLine($"[debug] Lexical pre-filter: {lexicalMatches.Count}, Semantic candidates requested: {_config.MaxResults}, returned: {semanticMatches.Count}");

        return MergeResults(lexicalMatches, semanticMatches, _config.MaxResults);
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

    private IReadOnlyList<LexicalMatch> SearchLexical(string query, int take)
    {
        var booleanQuery = new BooleanQuery();
        var escapedQuery = QueryParserBase.Escape(query);

        try
        {
            var symbolIdQuery = _symbolIdParser.Parse(escapedQuery);
            symbolIdQuery.Boost = IdentifierBoost;
            booleanQuery.Add(symbolIdQuery, Occur.SHOULD);
        }
        catch
        {
            // Ignore parse failures for the symbol ID field.
        }

        try
        {
            var textQuery = _textParser.Parse(escapedQuery);
            booleanQuery.Add(textQuery, Occur.SHOULD);
        }
        catch
        {
            // If text query fails and we have no other clauses, we can't proceed.
            if (booleanQuery.Clauses.Count == 0)
            {
                return Array.Empty<LexicalMatch>();
            }
        }

        if (booleanQuery.Clauses.Count == 0)
        {
            return Array.Empty<LexicalMatch>();
        }

        Query luceneQuery;
        if (!string.IsNullOrWhiteSpace(_config.Kind))
        {
            var filterQuery = new BooleanQuery();
            filterQuery.Add(booleanQuery, Occur.MUST);

            var kindLower = _config.Kind.ToLowerInvariant();
            if (kindLower == "csharp" || kindLower == "cs")
            {
                var langTerm = new Term(LuceneWriter.FieldLang, "csharp");
                filterQuery.Add(new TermQuery(langTerm), Occur.MUST);
            }
            else if (kindLower == "def" || kindLower == "xml")
            {
                var langTerm = new Term(LuceneWriter.FieldLang, "xml");
                filterQuery.Add(new TermQuery(langTerm), Occur.MUST);
            }

            luceneQuery = filterQuery;
        }
        else
        {
            luceneQuery = booleanQuery;
        }

        var hits = _searcher.Search(luceneQuery, take);
        if (hits.ScoreDocs.Length == 0)
        {
            return Array.Empty<LexicalMatch>();
        }

        var results = new List<LexicalMatch>(hits.ScoreDocs.Length);
        foreach (var scoreDoc in hits.ScoreDocs)
        {
            var doc = _searcher.Doc(scoreDoc.Doc);
            var symbolId = doc.Get(LuceneWriter.FieldSymbolId);
            if (string.IsNullOrEmpty(symbolId))
            {
                continue;
            }

            results.Add(new LexicalMatch(symbolId, doc, scoreDoc.Score));
        }

        return results;
    }

    private async Task<IReadOnlyList<VectorMatch>> SearchSemanticAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var queryVector = await _queryEmbeddingGenerator.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
            return _vectorIndex.FindNearest(queryVector, _config.SemanticCandidates);
        }
        catch
        {
            return Array.Empty<VectorMatch>();
        }
    }

    private IReadOnlyList<RoughSearchResult> MergeResults(IReadOnlyList<LexicalMatch> lexical, IReadOnlyList<VectorMatch> semantic, int take)
    {
        if (_config.UseSemanticScoringOnly)
        {
            // Pure semantic ranking: ignore lexical scores completely
            var maxScore = semantic.Count > 0 ? semantic[0].Score : 1f;
            
            return semantic
                .Take(take)
                .Select(match =>
                {
                    var document = FetchDocument(match.Entry.Id);
                    if (document is null) return null;
                    
                    return ToResult(match.Entry.Id, document, match.Score / maxScore, "semantic");
                })
                .Where(result => result is not null)
                .Select(result => result!)
                .ToList();
        }

        // Hybrid scoring (original behavior)
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        var lexicalMax = lexical.Count > 0 ? lexical[0].Score : 0f;
        var semanticMax = semantic.Count > 0 ? semantic[0].Score : 0f;

        foreach (var match in lexical)
        {
            if (!candidates.TryGetValue(match.SymbolId, out var candidate))
            {
                candidate = new Candidate(match.Document);
                candidates[match.SymbolId] = candidate;
            }

            candidate.SetLexical(match.Score, lexicalMax);
        }

        foreach (var match in semantic)
        {
            if (!candidates.TryGetValue(match.Entry.Id, out var candidate))
            {
                var document = FetchDocument(match.Entry.Id);
                if (document is null)
                {
                    continue;
                }

                candidate = new Candidate(document);
                candidates[match.Entry.Id] = candidate;
            }

            candidate.SetSemantic(match.Score, semanticMax);
        }

        foreach (var candidate in candidates.Values)
        {
            candidate.ComputeFinalScore(MixedBoost, SemanticWeight);
        }

        return candidates
            .OrderByDescending(kvp => kvp.Value.FinalScore)
            .Take(take)
            .Select(kvp => ToResultFromCandidate(kvp.Key, kvp.Value))
            .Where(result => result is not null)
            .Select(result => result!)
            .ToList();
    }

    private RoughSearchResult? ToResultFromCandidate(string symbolId, Candidate candidate)
    {
        var document = candidate.Document;
        return ToResult(symbolId, document, candidate.FinalScore, candidate switch
        {
            { HasLexical: true, HasSemantic: true } => "mixed",
            { HasLexical: true } => "lexical",
            { HasSemantic: true } => "semantic",
            _ => "unknown"
        });
    }

    private RoughSearchResult? ToResult(string symbolId, Document document, double score, string source)
    {
        var path = document.Get(LuceneWriter.FieldPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var langValue = document.Get(LuceneWriter.FieldLang);
        var language = langValue?.Equals("xml", StringComparison.OrdinalIgnoreCase) == true ? LanguageKind.Xml : LanguageKind.CSharp;

        var symbolKindValue = document.Get(LuceneWriter.FieldSymbolKind);
        if (!Enum.TryParse(symbolKindValue, true, out SymbolKind symbolKind))
        {
            symbolKind = SymbolKind.Unknown;
        }

        var preview = document.Get(LuceneWriter.FieldPreview) ?? string.Empty;
        var signature = document.Get(LuceneWriter.FieldSignature);
        var ns = document.Get(LuceneWriter.FieldNamespace);
        var containingType = document.Get(LuceneWriter.FieldClass);
        var spanStart = document.GetField(LuceneWriter.FieldSpanStart)?.GetInt32Value() ?? 0;
        var spanEnd = document.GetField(LuceneWriter.FieldSpanEnd)?.GetInt32Value() ?? 0;

        return new RoughSearchResult
        {
            SymbolId = symbolId,
            Path = path,
            Language = language,
            SymbolKind = symbolKind,
            Signature = signature,
            Preview = preview,
            Source = source,
            Score = score,
            Namespace = ns,
            ContainingType = containingType,
            SpanStart = spanStart,
            SpanEnd = spanEnd
        };
    }

    private Document? FetchDocument(string symbolId)
    {
        var query = new TermQuery(new Term(LuceneWriter.FieldSymbolId, symbolId));
        var hits = _searcher.Search(query, 1);
        if (hits.TotalHits == 0)
        {
            return null;
        }

        return _searcher.Doc(hits.ScoreDocs[0].Doc);
    }

    private IQueryEmbeddingGenerator CreateQueryEmbeddingGenerator()
    {
        var serverConfigured = !string.IsNullOrWhiteSpace(_config.EmbeddingServerUrl);
        //When apikey modelname exists, using api instead
        var apiConfigured =serverConfigured&& !string.IsNullOrWhiteSpace(_config.ApiKey) && !string.IsNullOrWhiteSpace(_config.ModelName);
        var pythonConfigured = !string.IsNullOrWhiteSpace(_config.PythonExecutablePath)
            && !string.IsNullOrWhiteSpace(_config.PythonScriptPath)
            && !string.IsNullOrWhiteSpace(_config.ModelPath);

        // Prefer server, fall back to subprocess
        if (serverConfigured || pythonConfigured)
        {
            try
            {
                return new AdaptiveEmbeddingGenerator(
                    serverUrl: _config.EmbeddingServerUrl,
                    pythonExecutable: _config.PythonExecutablePath,
                    scriptPath: _config.PythonScriptPath,
                    modelPath: _config.ModelPath);
            }
            catch
            {
                // Fall back to hash generator if configuration fails.
            }
        }

        var dimensions = _vectorIndex.VectorDimensions;
        if (dimensions <= 0)
        {
            dimensions = 768; // e5-base-v2 default
        }

    // If no embedding server or python subprocess is configured, prefer lexical-only behavior
    // and warn the user. Returning a generator that yields an empty vector causes the
    // semantic stage to be skipped and leaves lexical results intact.
    return new NoEmbeddingQueryEmbeddingGenerator();
    }

    public void Dispose()
    {
        (_queryEmbeddingGenerator as IDisposable)?.Dispose();
        _analyzer.Dispose();
        _reader.Dispose();
        _directory.Dispose();
    }

    private readonly record struct LexicalMatch(string SymbolId, Document Document, float Score);

    private sealed class Candidate
    {
        private float _lexicalScore;
        private float _semanticScore;

        public Candidate(Document document)
        {
            Document = document;
        }

        public Document Document { get; }
        public bool HasLexical { get; private set; }
        public bool HasSemantic { get; private set; }
        public double FinalScore { get; private set; }

        public void SetLexical(float score, float max)
        {
            HasLexical = true;
            _lexicalScore = max > 0 ? score / max : 0f;
        }

        public void SetSemantic(float score, float max)
        {
            HasSemantic = true;
            _semanticScore = max > 0 ? score / max : 0f;
        }

        public void ComputeFinalScore(float mixedBoost, float semanticWeight)
        {
            var combined = 0.0;
            if (HasLexical)
            {
                combined += _lexicalScore;
            }

            if (HasSemantic)
            {
                combined += _semanticScore * semanticWeight;
            }

            if (HasLexical && HasSemantic)
            {
                combined += mixedBoost;
            }

            FinalScore = combined;
        }
    }
}