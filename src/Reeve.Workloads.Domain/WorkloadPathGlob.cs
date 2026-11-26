using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;

namespace Reeve.Workloads.Domain;

/// <summary>Pure, path-aware glob matcher for the requirements path-subset check: <c>*</c> matches within
/// one path segment (never crosses <c>/</c>), <c>**</c> crosses segments, <c>?</c> is one non-<c>/</c> char. Anchored (whole path must match).</summary>
public static class WorkloadPathGlob
{
    /// <summary>True when <paramref name="path"/> is covered by <paramref name="pattern"/>; a null/empty path never matches.</summary>
    public static bool IsMatch(string pattern, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (string.IsNullOrEmpty(pattern))
            return false;

        if (pattern.IndexOfAny(WildcardChars) < 0)
            return string.Equals(pattern, path, StringComparison.Ordinal);

        return Matches(pattern.AsSpan(), path.AsSpan());
    }

    private static readonly char[] WildcardChars = ['*', '?'];

    private enum TokenKind : byte { Literal, AnyChar, StarSingle, StarDouble }

    private readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly char Literal;
        private Token(TokenKind kind, char literal) { Kind = kind; Literal = literal; }
        public static Token Of(char c) => new(TokenKind.Literal, c);
        public static readonly Token Any = new(TokenKind.AnyChar, '\0');
        public static readonly Token Single = new(TokenKind.StarSingle, '\0');
        public static readonly Token Double = new(TokenKind.StarDouble, '\0');
    }

    private static bool Matches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        Span<Token> tokens = pattern.Length <= 64 ? stackalloc Token[pattern.Length] : new Token[pattern.Length];
        var tokenCount = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var run = 0;
                while (i < pattern.Length && pattern[i] == '*') { run++; i++; }
                i--;
                tokens[tokenCount++] = run >= 2 ? Token.Double : Token.Single;
            }
            else if (c == '?')
            {
                tokens[tokenCount++] = Token.Any;
            }
            else
            {
                tokens[tokenCount++] = Token.Of(c);
            }
        }

        var m = text.Length;

        var next = new bool[m + 1];
        var cur = new bool[m + 1];
        next[m] = true;

        for (var ti = tokenCount - 1; ti >= 0; ti--)
        {
            var token = tokens[ti];
            switch (token.Kind)
            {
                case TokenKind.Literal:
                    var lit = token.Literal;
                    for (var t = 0; t <= m; t++)
                        cur[t] = t < m && text[t] == lit && next[t + 1];
                    break;

                case TokenKind.AnyChar:
                    for (var t = 0; t <= m; t++)
                        cur[t] = t < m && text[t] != '/' && next[t + 1];
                    break;

                case TokenKind.StarSingle:
                    cur[m] = next[m];
                    for (var t = m - 1; t >= 0; t--)
                        cur[t] = next[t] || (text[t] != '/' && cur[t + 1]);
                    break;

                case TokenKind.StarDouble:
                    cur[m] = next[m];
                    for (var t = m - 1; t >= 0; t--)
                        cur[t] = next[t] || cur[t + 1];
                    break;
            }

            (next, cur) = (cur, next);
        }

        return next[0];
    }
}
