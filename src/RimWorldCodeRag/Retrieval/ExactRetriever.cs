namespace RimWorldCodeRag.Retrieval;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Indexer;

//get_item工具通过标题返回整段代码
public sealed class ExactRetriever : IDisposable
{
    private const LuceneVersion LuceneVersion = Lucene.Net.Util.LuceneVersion.LUCENE_48;
    private readonly FSDirectory _directory;
    private readonly DirectoryReader _reader;
    private readonly IndexSearcher _searcher;
    private readonly StandardAnalyzer _analyzer;

    public ExactRetriever(string luceneIndexPath)
    {
        if (!System.IO.Directory.Exists(luceneIndexPath))
        {
            throw new DirectoryNotFoundException($"Lucene index not found at '{luceneIndexPath}'");
        }

        _directory = FSDirectory.Open(luceneIndexPath);
        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader);
        _analyzer = new StandardAnalyzer(LuceneVersion);
    }

    public ExactRetrievalResult? GetItem(string symbolId, int maxLines = 0)
    {
        // First try exact match
        var query = new TermQuery(new Term(LuceneWriter.FieldSymbolId, symbolId));
        var hits = _searcher.Search(query, 1);

        // If no exact match and symbolId starts with "xml:", try prefix match
        // This handles cases like "xml:StorytellerDef:Cassandra" -> "xml:StorytellerDef:Cassandra <- BaseStoryteller"
        if (hits.TotalHits == 0 && symbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
        {
            var prefixQuery = new PrefixQuery(new Term(LuceneWriter.FieldSymbolId, symbolId));
            hits = _searcher.Search(prefixQuery, 1);
        }

        // If still no match, try fuzzy structural search on symbol_id field
        if (hits.TotalHits == 0)
        {
            hits = FuzzySymbolSearch(symbolId);
        }

        if (hits.TotalHits == 0)
        {
            return null;
        }

        var doc = _searcher.Doc(hits.ScoreDocs[0].Doc);
        var actualSymbolId = doc.Get(LuceneWriter.FieldSymbolId) ?? symbolId;
        var filePath = doc.Get(LuceneWriter.FieldPath);
        var spanStartField = doc.GetField(LuceneWriter.FieldSpanStart);
        var spanEndField = doc.GetField(LuceneWriter.FieldSpanEnd);

        if (spanStartField == null || spanEndField == null)
        {
            throw new InvalidOperationException($"Symbol '{symbolId}' has missing span information");
        }

        var spanStart = spanStartField.GetInt32Value();
        var spanEnd = spanEndField.GetInt32Value();

        if (spanStart == null || spanEnd == null)
        {
            throw new InvalidOperationException($"Symbol '{symbolId}' has invalid span information");
        }

        string fullCode;
        var langStr = doc.Get(LuceneWriter.FieldLang);

        if (langStr == "xml")
        {
            var storedText = doc.Get(LuceneWriter.FieldStoredText);
            if (!string.IsNullOrEmpty(storedText))
            {
                fullCode = storedText;
            }
            else
            {//如果没结果，急眼了直接读文件
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Source file not found: {filePath}");
                }
                var sourceText = File.ReadAllText(filePath);
                fullCode = spanStart.Value >= 0 && spanEnd.Value <= sourceText.Length 
                    ? sourceText.Substring(spanStart.Value, spanEnd.Value - spanStart.Value)
                    : sourceText; 
            }
        }
        else
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Source file not found: {filePath}");
            }

            var sourceText = File.ReadAllText(filePath);

            if (spanStart.Value < 0 || spanEnd.Value > sourceText.Length || spanStart.Value >= spanEnd.Value)
            {
                throw new InvalidOperationException($"Symbol '{symbolId}' has invalid span [{spanStart.Value}, {spanEnd.Value}] for file length {sourceText.Length}");
            }

            fullCode = sourceText.Substring(spanStart.Value, spanEnd.Value - spanStart.Value);
        }

        //行数限制
        string displayCode = fullCode;
        bool truncated = false;
        var lines = fullCode.Split('\n');
        
        if (maxLines > 0 && lines.Length > maxLines)
        {
            displayCode = string.Join('\n', lines.Take(maxLines));
            truncated = true;
        }

        var language = langStr == "csharp" ? LanguageKind.CSharp : LanguageKind.Xml;

        var symbolKindStr = doc.Get(LuceneWriter.FieldSymbolKind);
        var symbolKind = ParseSymbolKind(symbolKindStr);

        return new ExactRetrievalResult
        {
            SymbolId = actualSymbolId,
            Path = filePath,
            Language = language,
            SymbolKind = symbolKind,
            Namespace = doc.Get(LuceneWriter.FieldNamespace),
            ContainingType = doc.Get(LuceneWriter.FieldClass),
            Signature = doc.Get(LuceneWriter.FieldSignature),
            DefType = doc.Get(LuceneWriter.FieldDefType),
            SourceCode = displayCode,
            FullCode = fullCode,
            Truncated = truncated,
            TotalLines = lines.Length,
            DisplayedLines = displayCode.Split('\n').Length
        };
    }

    /// <summary>
    /// Fuzzy search using structural matching.
    /// Prioritizes: 1) Type/Class matches over methods, 2) Symbol ID containing query parts
    /// When query looks like Namespace.Type, tries to find the Type even if namespace is wrong.
    /// </summary>
    private TopDocs FuzzySymbolSearch(string symbolId)
    {
        try
        {
            // Split the query into structural parts
            var queryParts = symbolId.Split(new[] { ':', '.', ' ', '<', '-', '_', '>' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length >= 2)
                .Select(p => p.ToLowerInvariant())
                .ToList();

            if (queryParts.Count == 0)
            {
                return new TopDocs(0, Array.Empty<ScoreDoc>(), 0);
            }

            var boolQuery = new BooleanQuery();
            
            // Determine if this looks like an XML query
            var isXmlQuery = symbolId.StartsWith("xml", StringComparison.OrdinalIgnoreCase) 
                          || queryParts.Any(p => p == "xml" || p == "def");
            
            // For XML queries, filter to xml language
            if (isXmlQuery)
            {
                boolQuery.Add(new TermQuery(new Term(LuceneWriter.FieldLang, "xml")), Occur.MUST);
                // Remove "xml" from parts since it's handled by lang filter
                queryParts = queryParts.Where(p => p != "xml").ToList();
            }
            
            // Check if this looks like a Namespace.Type pattern (2+ parts with dots)
            var looksLikeNamespaceType = !isXmlQuery && queryParts.Count >= 2 && symbolId.Contains('.');
            
            // The last part is the most important (the Type name) - MUST match
            var typeName = queryParts.Last();
            
            if (looksLikeNamespaceType)
            {
                // Search for exact type name match in symbol_id
                // This catches "Verse.Pawn" when user queries "RimWorld.Pawn"
                var typeQuery = new WildcardQuery(new Term(LuceneWriter.FieldSymbolId, $"*.{typeName}"));
                boolQuery.Add(typeQuery, Occur.MUST);
                
                // Also try to match in identifiers_kw
                var idKwQuery = new TermQuery(new Term(LuceneWriter.FieldIdentifiersKw, typeName));
                boolQuery.Add(idKwQuery, Occur.SHOULD);
            }
            else
            {
                // For single-word queries like "Pawn", try both exact type match and substring
                var typeMatchQuery = new BooleanQuery();
                
                // Prefer symbol_id ending with ".typename" (exact type name)
                var exactTypeQuery = new WildcardQuery(new Term(LuceneWriter.FieldSymbolId, $"*.{typeName}"));
                typeMatchQuery.Add(exactTypeQuery, Occur.SHOULD);
                
                // Also allow general substring match
                var substringQuery = new WildcardQuery(new Term(LuceneWriter.FieldSymbolId, $"*{typeName}*"));
                typeMatchQuery.Add(substringQuery, Occur.SHOULD);
                
                boolQuery.Add(typeMatchQuery, Occur.MUST);
                
                // Other parts are optional but boost score
                foreach (var part in queryParts.SkipLast(1))
                {
                    var partQuery = new WildcardQuery(new Term(LuceneWriter.FieldSymbolId, $"*{part}*"));
                    boolQuery.Add(partQuery, Occur.SHOULD);
                }
            }

            if (boolQuery.Clauses.Count == 0)
            {
                return new TopDocs(0, Array.Empty<ScoreDoc>(), 0);
            }

            // Get multiple candidates and score them
            var hits = _searcher.Search(boolQuery, 100);
            if (hits.TotalHits == 0)
            {
                return hits;
            }

            // Re-rank candidates: prioritize Type over Method, and symbol ID that ends with the query type
            var candidates = new List<(ScoreDoc scoreDoc, double rank, string symbolId)>();
            var queryTypeName = queryParts.LastOrDefault()?.ToLowerInvariant() ?? "";
            
            foreach (var scoreDoc in hits.ScoreDocs)
            {
                var doc = _searcher.Doc(scoreDoc.Doc);
                var docSymbolId = doc.Get(LuceneWriter.FieldSymbolId) ?? "";
                var docSymbolKind = doc.Get(LuceneWriter.FieldSymbolKind) ?? "";
                
                double rank = 0;
                var docSymbolLower = docSymbolId.ToLowerInvariant();
                
                // VERY high bonus for Type/Class
                if (docSymbolKind.Equals("Type", StringComparison.OrdinalIgnoreCase))
                {
                    rank += 1000.0;
                    
                    // Extra bonus if symbol_id ends with the query type name
                    // e.g., "Verse.Pawn" ends with "pawn"
                    if (docSymbolLower.EndsWith($".{queryTypeName}") || docSymbolLower == queryTypeName)
                    {
                        rank += 500.0;
                    }
                    else if (docSymbolLower.EndsWith(queryTypeName))
                    {
                        rank += 200.0;
                    }
                }
                else if (docSymbolKind.Equals("Property", StringComparison.OrdinalIgnoreCase))
                {
                    rank += 50.0;
                }
                else if (docSymbolKind.Equals("Field", StringComparison.OrdinalIgnoreCase))
                {
                    rank += 40.0;
                }
                else if (docSymbolKind.Equals("Method", StringComparison.OrdinalIgnoreCase))
                {
                    rank += 10.0;
                }
                
                // Bonus for all query parts appearing in the symbol
                var matchedParts = queryParts.Count(p => docSymbolLower.Contains(p));
                rank += matchedParts * 20.0;
                
                // Penalty for long symbol IDs (methods with parameters are longer)
                rank -= docSymbolId.Length * 0.2;
                
                // Penalty if symbol contains parentheses (method signature)
                if (docSymbolId.Contains('('))
                {
                    rank -= 200.0;
                }
                
                candidates.Add((scoreDoc, rank, docSymbolId));
            }
            
            // Sort by rank descending
            candidates.Sort((a, b) => b.rank.CompareTo(a.rank));
            
            // Return the best candidate
            if (candidates.Count > 0)
            {
                var bestCandidate = candidates.First();
                return new TopDocs(1, new[] { bestCandidate.scoreDoc }, (float)bestCandidate.rank);
            }
            
            return new TopDocs(0, Array.Empty<ScoreDoc>(), 0);
        }
        catch
        {
            return new TopDocs(0, Array.Empty<ScoreDoc>(), 0);
        }
    }

    //多个集中提取，这个暂时没用上，先做个实现，看看效果再说
    public IReadOnlyList<ExactRetrievalResult> GetItems(IEnumerable<string> symbolIds, int maxLines = 0)
    {
        var results = new List<ExactRetrievalResult>();

        foreach (var symbolId in symbolIds)
        {
            try
            {
                var result = GetItem(symbolId, maxLines);
                if (result != null)
                {
                    results.Add(result);
                }
            }
            catch
            {
                continue;
            }
        }

        return results;
    }

    private static SymbolKind ParseSymbolKind(string? kindStr)
    {
        if (string.IsNullOrWhiteSpace(kindStr))
        {
            return SymbolKind.Unknown;
        }

        return kindStr.ToLowerInvariant() switch
        {
            "namespace" => SymbolKind.Namespace,
            "type" => SymbolKind.Type,
            "method" => SymbolKind.Method,
            "property" => SymbolKind.Property,
            "field" => SymbolKind.Field,
            "constructor" => SymbolKind.Constructor,
            "xmldef" => SymbolKind.XmlDef,
            _ => SymbolKind.Unknown
        };
    }

    public void Dispose()
    {
        _analyzer?.Dispose();
        _reader?.Dispose();
        _directory?.Dispose();
    }
}
