using System.Text.RegularExpressions;

namespace UE5LibraryBrowser;

internal sealed class SearchQuery
{
    private static readonly Regex TokenRegex = new("\"([^\"]+)\"|'([^']+)'|(\\S+)", RegexOptions.Compiled);
    private readonly List<SearchTerm> _terms;

    private SearchQuery(List<SearchTerm> terms)
    {
        _terms = terms;
    }

    public static SearchQuery Parse(string text)
    {
        var terms = new List<SearchTerm>();
        if (string.IsNullOrWhiteSpace(text))
            return new SearchQuery(terms);

        foreach (Match match in TokenRegex.Matches(text))
        {
            var raw = match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value;

            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var exclude = raw.StartsWith("-", StringComparison.Ordinal) && raw.Length > 1;
            if (exclude)
                raw = raw[1..];

            raw = raw.Trim();
            if (raw.Length == 0)
                continue;

            terms.Add(new SearchTerm(raw, exclude, CreateWildcardRegex(raw)));
        }

        return new SearchQuery(terms);
    }

    public bool IsEmpty => _terms.Count == 0;

    public bool Matches(params string[] values)
        => Matches((IEnumerable<string>)values);

    public bool Matches(IEnumerable<string> values)
    {
        if (_terms.Count == 0)
            return true;

        var materialized = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        foreach (var term in _terms)
        {
            var matched = materialized.Any(term.IsMatch);
            if (term.Exclude ? matched : !matched)
                return false;
        }

        return true;
    }

    private static Regex? CreateWildcardRegex(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return null;

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed class SearchTerm
    {
        private readonly string _pattern;
        private readonly Regex? _wildcardRegex;

        public SearchTerm(string pattern, bool exclude, Regex? wildcardRegex)
        {
            _pattern = pattern;
            Exclude = exclude;
            _wildcardRegex = wildcardRegex;
        }

        public bool Exclude { get; }

        public bool IsMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return _wildcardRegex != null
                ? _wildcardRegex.IsMatch(value)
                : value.IndexOf(_pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
