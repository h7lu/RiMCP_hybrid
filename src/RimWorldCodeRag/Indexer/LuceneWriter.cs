using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

internal sealed class LuceneWriter : IDisposable
{
    public const string FieldPath = "path";
    public const string FieldLang = "lang";
    public const string FieldNamespace = "namespace";
    public const string FieldClass = "class";
    public const string FieldSymbolKind = "symbol_kind";
    public const string FieldSymbolId = "symbol_id";
    public const string FieldSignature = "signature";
    public const string FieldIdentifiers = "identifiers";
    public const string FieldIdentifiersKw = "identifiers_kw";
    public const string FieldText = "text";
    public const string FieldPreview = "preview";
    public const string FieldSpanStart = "span_start";
    public const string FieldSpanEnd = "span_end";
    public const string FieldXmlLinks = "xml_links";
    public const string FieldDefType = "def_type";
    public const string FieldStoredText = "stored_text"; // For XML defs: stores full text

    private readonly IndexWriter _writer;
    private readonly Dictionary<string, List<ChunkRecord>> _duplicateTracker = new();
    private readonly string _duplicateLogPath;

    public LuceneWriter(string indexPath)
    {
        System.IO.Directory.CreateDirectory(indexPath);
        var directory = FSDirectory.Open(indexPath);
        var analyzer = CreateAnalyzer();
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND
        };

        _writer = new IndexWriter(directory, config);
        _duplicateLogPath = Path.Combine(Path.GetTempPath(), "rimworld_duplicate_keys.txt");
    }    public void IndexDocuments(IEnumerable<ChunkRecord> chunks)
    {
        foreach (var chunk in chunks)
        {
            var doc = new Document
            {
                new StringField(FieldPath, chunk.Path, Field.Store.YES),
                new StringField(FieldLang, chunk.Language == LanguageKind.CSharp ? "csharp" : "xml", Field.Store.YES),
                new StringField(FieldNamespace, chunk.Namespace ?? string.Empty, Field.Store.YES),
                new StringField(FieldClass, chunk.ContainingType ?? string.Empty, Field.Store.YES),
                new StringField(FieldSymbolKind, chunk.SymbolKind.ToString().ToLowerInvariant(), Field.Store.YES),
                new StringField(FieldSymbolId, chunk.Id, Field.Store.YES),
                new TextField(FieldSymbolId, chunk.Id, Field.Store.NO),
                new TextField(FieldText, chunk.Text, Field.Store.NO),
                new StoredField(FieldPreview, chunk.Preview),
                new Int32Field(FieldSpanStart, chunk.SpanStart, Field.Store.YES),
                new Int32Field(FieldSpanEnd, chunk.SpanEnd, Field.Store.YES)
            };

            if (!string.IsNullOrWhiteSpace(chunk.Signature))
            {
                doc.Add(new StringField(FieldSignature, chunk.Signature!, Field.Store.YES));
            }

            foreach (var id in chunk.KeywordIdentifiers)
            {
                doc.Add(new StringField(FieldIdentifiersKw, id, Field.Store.YES));
            }

            if (chunk.Identifiers.Length > 0)
            {
                doc.Add(new TextField(FieldIdentifiers, string.Join(' ', chunk.Identifiers), Field.Store.NO));
            }

            if (chunk.XmlLinks.Length > 0)
            {
                doc.Add(new StringField(FieldXmlLinks, string.Join(',', chunk.XmlLinks), Field.Store.YES));
            }

            // Add DefType field for XML Defs (enables filtering by DefType)
            if (!string.IsNullOrWhiteSpace(chunk.DefType))
            {
                doc.Add(new StringField(FieldDefType, chunk.DefType, Field.Store.YES));
            }

            // For XML defs, store the full text since SpanStart/SpanEnd don't work with element.ToString()
            if (chunk.Language == LanguageKind.Xml)
            {
                doc.Add(new StoredField(FieldStoredText, chunk.Text));
            }

            // Track duplicates before updating
            TrackDuplicate(chunk);
            _writer.UpdateDocument(new Term(FieldSymbolId, chunk.Id), doc);
        }
    }

    public void Reset()
    {
        _writer.DeleteAll();
    }

    public void Commit()
    {
        _writer.Flush(false, false);
        _writer.Commit();
    }

    public void Dispose()
    {
        WriteDuplicateLog();
        _writer.Dispose();
    }

    private void TrackDuplicate(ChunkRecord chunk)
    {
        if (!_duplicateTracker.ContainsKey(chunk.Id))
        {
            _duplicateTracker[chunk.Id] = new List<ChunkRecord>();
        }
        _duplicateTracker[chunk.Id].Add(chunk);
    }

    private void WriteDuplicateLog()
    {
        var duplicates = _duplicateTracker.Where(kvp => kvp.Value.Count > 1).ToList();
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[LuceneWriter] Processed {_duplicateTracker.Count} SymbolIds, found {duplicates.Count} duplicates");
        Console.ResetColor();
        
        if (!duplicates.Any())
        {
            File.WriteAllText(_duplicateLogPath, "No duplicate SymbolIds found during indexing.\n");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[LuceneWriter] No duplicates found. Log written to: {_duplicateLogPath}");
            Console.ResetColor();
            return;
        }

        using var writer = new StreamWriter(_duplicateLogPath, append: false);
        Console.ForegroundColor = ConsoleColor.Yellow;
        writer.WriteLine("RimWorld RAG - Duplicate SymbolId Detection Report");
        writer.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"Total SymbolIds processed: {_duplicateTracker.Count}");
        writer.WriteLine($"Duplicate SymbolIds found: {duplicates.Count}");
        writer.WriteLine(new string('=', 80));
        writer.WriteLine();
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.ResetColor();

        foreach (var duplicate in duplicates.OrderBy(kvp => kvp.Key))
        {
            var symbolId = duplicate.Key;
            var chunks = duplicate.Value;
            
            Console.ForegroundColor = ConsoleColor.Red;
            writer.WriteLine($"SymbolId: {symbolId}");
            writer.WriteLine($"Occurrences: {chunks.Count}");
            writer.WriteLine(new string('-', 40));
            Console.ResetColor();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                Console.ForegroundColor = ConsoleColor.White;
                writer.WriteLine($"  [{i + 1}] Path: {chunk.Path}");
                writer.WriteLine($"      Language: {chunk.Language}");
                writer.WriteLine($"      SymbolKind: {chunk.SymbolKind}");
                writer.WriteLine($"      DefType: {chunk.DefType ?? "N/A"}");
                writer.WriteLine($"      Signature: {chunk.Signature ?? "N/A"}");
                writer.WriteLine($"      Preview: {(chunk.Preview?.Length > 100 ? chunk.Preview.Substring(0, 100) + "..." : chunk.Preview ?? "N/A")}");
                Console.ResetColor();
                writer.WriteLine();
            }
            writer.WriteLine();
        }

        writer.WriteLine(new string('=', 80));
        writer.WriteLine("Analysis Summary:");
        writer.WriteLine();
        
        // Group by language
        var byLanguage = duplicates.SelectMany(kvp => kvp.Value)
            .GroupBy(chunk => chunk.Language)
            .OrderBy(g => g.Key);
            
        Console.ForegroundColor = ConsoleColor.Cyan;
        foreach (var group in byLanguage)
        {
            writer.WriteLine($"  {group.Key}: {group.Count()} duplicate occurrences");
        }
        Console.ResetColor();
        
        writer.WriteLine();
        Console.ForegroundColor = ConsoleColor.Gray;
        writer.WriteLine("Note: Lucene UpdateDocument() replaces previous documents with same SymbolId.");
        writer.WriteLine("Only: last occurrence of each duplicate SymbolId will be searchable.");
        Console.ResetColor();
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[LuceneWriter] Duplicate report written to: {_duplicateLogPath}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[LuceneWriter] Processed {_duplicateTracker.Count} SymbolIds, found and resolved {duplicates.Count} duplicates");
        Console.ResetColor();
    }

    private static Analyzer CreateAnalyzer()
    {
        var defaultAnalyzer = new Lucene.Net.Analysis.Standard.StandardAnalyzer(LuceneVersion.LUCENE_48);
        var perField = new PerFieldAnalyzerWrapper(defaultAnalyzer, new Dictionary<string, Analyzer>
        {
            [FieldIdentifiersKw] = new KeywordAnalyzer(),
            [FieldIdentifiers] = new WhitespaceAnalyzer(LuceneVersion.LUCENE_48)
        });

        return perField;
    }
}
