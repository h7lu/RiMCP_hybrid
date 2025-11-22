namespace RimWorldCodeRag.Retrieval;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Indexer;

//get_item工具通过标题返回整段代码
public sealed class ExactRetriever : IDisposable
{
    private readonly FSDirectory _directory;
    private readonly DirectoryReader _reader;
    private readonly IndexSearcher _searcher;

    public ExactRetriever(string luceneIndexPath)
    {
        if (!System.IO.Directory.Exists(luceneIndexPath))
        {
            throw new DirectoryNotFoundException($"Lucene index not found at '{luceneIndexPath}'");
        }

        _directory = FSDirectory.Open(luceneIndexPath);
        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader);
    }

    public ExactRetrievalResult? GetItem(string symbolId, int maxLines = 0)
    {
        var query = new TermQuery(new Term(LuceneWriter.FieldSymbolId, symbolId));
        var hits = _searcher.Search(query, 1);

        if (hits.TotalHits == 0)
        {
            return null;
        }

        var doc = _searcher.Doc(hits.ScoreDocs[0].Doc);
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
            SymbolId = symbolId,
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
        _reader?.Dispose();
        _directory?.Dispose();
    }
}
