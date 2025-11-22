using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RimWorldCodeRag.Indexer;

internal sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _addedTokens;
    private readonly string _unkToken;
    private readonly string _continuingSubwordPrefix;
    private readonly int _maxInputCharsPerWord;
    private readonly bool _lowercase;
    private readonly bool _cleanText;
    private readonly bool _handleChineseChars;
    private readonly bool _stripAccents;
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;
    private readonly int _padTokenId;

    public WordPieceTokenizer(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath))
        {
            throw new FileNotFoundException("tokenizer.json not found", tokenizerJsonPath);
        }

        using var stream = File.OpenRead(tokenizerJsonPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var model = root.GetProperty("model");
        _unkToken = model.GetProperty("unk_token").GetString() ?? "[UNK]";
        _continuingSubwordPrefix = model.TryGetProperty("continuing_subword_prefix", out var prefixEl)
            ? prefixEl.GetString() ?? "##"
            : "##";
        _maxInputCharsPerWord = model.TryGetProperty("max_input_chars_per_word", out var maxCharsEl)
            ? maxCharsEl.GetInt32()
            : 100;

        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in model.GetProperty("vocab").EnumerateObject())
        {
            _vocab[property.Name] = property.Value.GetInt32();
        }

        _addedTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("added_tokens", out var addedTokensEl))
        {
            foreach (var tok in addedTokensEl.EnumerateArray())
            {
                var content = tok.GetProperty("content").GetString();
                if (content is null)
                {
                    continue;
                }

                var id = tok.GetProperty("id").GetInt32();
                _addedTokens[content] = id;
            }
        }

        _cleanText = root.TryGetProperty("normalizer", out var normalizerEl)
            && normalizerEl.TryGetProperty("clean_text", out var cleanEl)
            && cleanEl.GetBoolean();

        _handleChineseChars = root.TryGetProperty("normalizer", out normalizerEl)
            && normalizerEl.TryGetProperty("handle_chinese_chars", out var chineseEl)
            && chineseEl.GetBoolean();

        _stripAccents = root.TryGetProperty("normalizer", out normalizerEl)
            && normalizerEl.TryGetProperty("strip_accents", out var stripEl)
            && stripEl.ValueKind == JsonValueKind.True;

        _lowercase = root.TryGetProperty("normalizer", out normalizerEl)
            && normalizerEl.TryGetProperty("lowercase", out var lowerEl)
            && lowerEl.GetBoolean();

        _clsTokenId = ResolveTokenId("[CLS]");
        _sepTokenId = ResolveTokenId("[SEP]");
        _padTokenId = ResolveTokenId("[PAD]");
    }

    public TokenizationResult Encode(string text, int maxTokens)
    {
        if (maxTokens < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "maxTokens must be at least 2 to include CLS and SEP tokens.");
        }

        text ??= string.Empty;
        var processed = _cleanText ? CleanText(text) : text;

        if (_handleChineseChars)
        {
            processed = HandleChineseCharacters(processed);
        }

        if (_lowercase)
        {
            processed = processed.ToLowerInvariant();
        }

        if (_stripAccents)
        {
            processed = StripAccents(processed);
        }

        var basicTokens = new List<string>();
        foreach (var token in WhitespaceTokenize(processed))
        {
            basicTokens.AddRange(SplitOnPunctuation(token));
        }

        var wordPieceIds = new List<int>(maxTokens);
        foreach (var token in basicTokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            if (token.Length > _maxInputCharsPerWord)
            {
                wordPieceIds.Add(ResolveTokenId(_unkToken));
                continue;
            }

            var start = 0;
            var isBad = false;
            var subTokens = new List<int>();

            while (start < token.Length)
            {
                var end = token.Length;
                int? curSubTokenId = null;

                while (start < end)
                {
                    var substr = token.Substring(start, end - start);
                    if (start > 0)
                    {
                        substr = _continuingSubwordPrefix + substr;
                    }

                    if (TryResolveTokenId(substr, out var subId))
                    {
                        curSubTokenId = subId;
                        start = end;
                        subTokens.Add(subId);
                        break;
                    }

                    end--;
                }

                if (curSubTokenId is null)
                {
                    isBad = true;
                    break;
                }
            }

            if (isBad)
            {
                wordPieceIds.Add(ResolveTokenId(_unkToken));
            }
            else
            {
                wordPieceIds.AddRange(subTokens);
            }
        }

        var tokensCapacity = Math.Min(wordPieceIds.Count, maxTokens - 2);
        var actualTokens = new List<int>(tokensCapacity + 2)
        {
            _clsTokenId
        };

        actualTokens.AddRange(wordPieceIds.Take(tokensCapacity));
        actualTokens.Add(_sepTokenId);

        var attentionLength = Math.Min(actualTokens.Count, maxTokens);
        var inputIds = new int[maxTokens];
        var attentionMask = new int[maxTokens];

        for (int i = 0; i < attentionLength; i++)
        {
            inputIds[i] = actualTokens[i];
            attentionMask[i] = 1;
        }

        for (int i = attentionLength; i < maxTokens; i++)
        {
            inputIds[i] = _padTokenId;
            attentionMask[i] = 0;
        }

        return new TokenizationResult(inputIds, attentionMask, attentionLength);
    }

    private int ResolveTokenId(string token)
    {
        if (TryResolveTokenId(token, out var id))
        {
            return id;
        }

        throw new InvalidOperationException($"Token '{token}' not found in vocabulary.");
    }

    private bool TryResolveTokenId(string token, out int id)
    {
        if (_vocab.TryGetValue(token, out id))
        {
            return true;
        }

        if (_addedTokens.TryGetValue(token, out id))
        {
            return true;
        }

        id = default;
        return false;
    }

    private static string CleanText(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (ch == 0 || ch == 0xFFFD)
            {
                continue;
            }

            if (IsControl(ch))
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string HandleChineseCharacters(string text)
    {
        var sb = new StringBuilder(text.Length * 2);

        foreach (var ch in text)
        {
            if (IsChineseChar(ch))
            {
                sb.Append(' ');
                sb.Append(ch);
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string StripAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> WhitespaceTokenize(string text)
    {
        var tokens = new List<string>();
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        tokens.AddRange(parts);
        return tokens;
    }

    private static IEnumerable<string> SplitOnPunctuation(string token)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        foreach (var ch in token)
        {
            if (IsPunctuation(ch))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }

                result.Add(ch.ToString());
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
        }

        return result;
    }

    private static bool IsControl(char ch)
    {
        if (ch == '\t' || ch == '\n' || ch == '\r')
        {
            return false;
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
        return cat == UnicodeCategory.Control || cat == UnicodeCategory.Format;
    }

    private static bool IsPunctuation(char ch)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
        if (cat == UnicodeCategory.ConnectorPunctuation ||
            cat == UnicodeCategory.DashPunctuation ||
            cat == UnicodeCategory.OpenPunctuation ||
            cat == UnicodeCategory.ClosePunctuation ||
            cat == UnicodeCategory.InitialQuotePunctuation ||
            cat == UnicodeCategory.FinalQuotePunctuation ||
            cat == UnicodeCategory.OtherPunctuation)
        {
            return true;
        }

        return (ch >= 33 && ch <= 47) ||
               (ch >= 58 && ch <= 64) ||
               (ch >= 91 && ch <= 96) ||
               (ch >= 123 && ch <= 126);
    }

    private static bool IsChineseChar(char ch)
    {
        return (ch >= 0x4E00 && ch <= 0x9FFF) ||
               (ch >= 0x3400 && ch <= 0x4DBF) ||
               (ch >= 0x20000 && ch <= 0x2A6DF) ||
               (ch >= 0x2A700 && ch <= 0x2B73F) ||
               (ch >= 0x2B740 && ch <= 0x2B81F) ||
               (ch >= 0x2B820 && ch <= 0x2CEAF) ||
               (ch >= 0xF900 && ch <= 0xFAFF) ||
               (ch >= 0x2F800 && ch <= 0x2FA1F);
    }
}

internal readonly record struct TokenizationResult(int[] InputIds, int[] AttentionMask, int SequenceLength);