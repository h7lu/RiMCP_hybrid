using System.Text;
using System.Text.RegularExpressions;

namespace RimWorldCodeRag.Common;

public static partial class TextUtilities
{
    private static readonly Regex CamelCaseBoundary = CamelCaseBoundaryRegex();
    private static readonly Regex NonIdentifier = NonIdentifierRegex();

    public static IEnumerable<string> SplitIdentifiers(IEnumerable<string> identifiers)
    {
        foreach (var id in identifiers)
        {
            foreach (var token in SplitIdentifier(id))
            {
                if (token.Length > 0)
                {
                    yield return token.ToLowerInvariant();
                }
            }
        }
    }

    public static IEnumerable<string> SplitIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            yield break;
        }

        var sanitized = NonIdentifier.Replace(identifier, " ");
        foreach (var raw in sanitized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var lowered = raw.ToLowerInvariant();
            if (lowered.Length == 0)
            {
                continue;
            }

            yield return lowered;

            foreach (var sub in CamelCaseBoundary.Split(raw))
            {
                var subLower = sub.ToLowerInvariant();
                if (subLower.Length > 0 && subLower != lowered)
                {
                    yield return subLower;
                }
            }
        }
    }

    public static string BuildPreview(string text, int maxLength = 320)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var sb = new StringBuilder(trimmed, 0, Math.Min(trimmed.Length, maxLength), maxLength + 16);
        sb.Append(" …");
        return sb.ToString();
    }

    [GeneratedRegex("(?<!^)(?=[A-Z][a-z])|(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryRegex();

    [GeneratedRegex("[^A-Za-z0-9_\\.]", RegexOptions.Compiled)]
    private static partial Regex NonIdentifierRegex();
}
