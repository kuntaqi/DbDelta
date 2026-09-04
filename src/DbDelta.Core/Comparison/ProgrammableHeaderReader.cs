using System.Text;
using DbDelta.Core.Model;

namespace DbDelta.Core.Comparison;

// Reads the name out of a stored CREATE VIEW / PROCEDURE / FUNCTION / TRIGGER, so the name the catalog
// holds can be compared against the name the body carries. Two callers need this and they sit on opposite
// sides of the provider boundary — the emitter has to write the catalog's name, the comparer has to stop
// caring about the body's — so it lives here in Core rather than in the SQL Server provider.
// SqlBodyNormalizer set that precedent already: T-SQL-shaped text handling, engine-neutral home.
//
// It parses only as far as the name and no further. Anything it does not recognise comes back as null,
// and the caller's job is then to say so rather than to guess.
public static class ProgrammableHeaderReader
{
    // PROCEDURE before PROC only for readability — the word match checks the boundary, so PROC cannot
    // match the front of PROCEDURE either way.
    private static readonly string[] Keywords = ["VIEW", "PROCEDURE", "PROC", "FUNCTION", "TRIGGER"];

    // The words that can legally follow the name. An undelimited one of these is not an identifier, so
    // reading it as part of the name would invent a name out of a body that has none — "CREATE VIEW dbo."
    // would come back as an object called AS. The server cannot store a definition that malformed, but
    // failing closed hands it to the caller as unreadable instead of quietly making something up.
    private static readonly HashSet<string> Following =
        new(["AS", "ON", "WITH", "FOR", "AFTER", "INSTEAD", "RETURNS"], StringComparer.OrdinalIgnoreCase);

    public static ProgrammableHeader? Read(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var i = FirstStatement(definition);

        if (!Word(definition, ref i, "CREATE"))
        {
            return null;
        }

        SkipTrivia(definition, ref i);

        // SQL Server stores a CREATE OR ALTER with the OR ALTER blanked out, but a definition can also
        // arrive here still carrying it — anything not read back from sys.sql_modules, a test among them.
        var beforeOrAlter = i;
        if (Word(definition, ref i, "OR"))
        {
            SkipTrivia(definition, ref i);

            if (!Word(definition, ref i, "ALTER"))
            {
                return null;
            }

            SkipTrivia(definition, ref i);
        }
        else
        {
            i = beforeOrAlter;
        }

        if (!Keywords.Any(keyword => Word(definition, ref i, keyword)))
        {
            return null;
        }

        SkipTrivia(definition, ref i);

        return ReadName(definition, i);
    }

    // The definition with its header name replaced by the catalog's, for comparison only. Both sides get
    // the same replacement, so what a body says about its own name — a stale name, a missing schema,
    // brackets or no brackets — stops being a difference. None of the object's behaviour is in there: the
    // name it answers to is the one in the catalog, and that is already how the two sides were paired.
    public static string WithCanonicalName(string definition, ObjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);

        return Read(definition) is { } header
            ? header.ReplaceIn(definition, identity.QualifiedName)
            : definition;
    }

    // Index of the first thing that is not whitespace or a comment.
    public static int FirstStatement(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var i = 0;
        SkipTrivia(text, ref i);
        return i;
    }

    // T-SQL block comments nest, so the depth is counted rather than scanning for the first close.
    private static void SkipTrivia(string text, ref int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '-' && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                var depth = 1;
                i += 2;

                while (i < text.Length && depth > 0)
                {
                    if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }

                continue;
            }

            break;
        }
    }

    private static bool Word(string text, ref int i, string word)
    {
        var end = i + word.Length;

        if (end > text.Length || !text.AsSpan(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (end < text.Length && IsNameChar(text[end]))
        {
            return false;
        }

        i = end;
        return true;
    }

    // Only the last two parts matter: CREATE refuses a database prefix, so anything longer is a shape this
    // parser does not understand, and the extra parts are left to fail the identity check rather than
    // being guessed at.
    private static ProgrammableHeader? ReadName(string text, int start)
    {
        var parts = new List<string>();
        var i = start;

        while (true)
        {
            if (!ReadPart(text, ref i, out var part))
            {
                return null;
            }

            parts.Add(part);

            var beforeDot = i;
            SkipTrivia(text, ref i);

            if (i >= text.Length || text[i] != '.')
            {
                i = beforeDot;
                break;
            }

            i++;
            SkipTrivia(text, ref i);
        }

        return new ProgrammableHeader(
            start,
            i - start,
            parts.Count > 1 ? parts[^2] : null,
            parts[^1]);
    }

    private static bool ReadPart(string text, ref int i, out string part)
    {
        part = string.Empty;

        if (i >= text.Length)
        {
            return false;
        }

        if (text[i] == '[')
        {
            return ReadDelimited(text, ref i, ']', out part);
        }

        if (text[i] == '"')
        {
            return ReadDelimited(text, ref i, '"', out part);
        }

        var begin = i;
        while (i < text.Length && IsNameChar(text[i]))
        {
            i++;
        }

        if (i == begin)
        {
            return false;
        }

        var bare = text[begin..i];

        if (Following.Contains(bare))
        {
            i = begin;
            return false;
        }

        part = bare;
        return true;
    }

    // Both delimiters double to escape themselves, so [a]]b] is one part named a]b.
    private static bool ReadDelimited(string text, ref int i, char close, out string part)
    {
        part = string.Empty;
        var value = new StringBuilder();
        i++;

        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (i + 1 < text.Length && text[i + 1] == close)
                {
                    value.Append(close);
                    i += 2;
                    continue;
                }

                i++;
                part = value.ToString();
                return part.Length > 0;
            }

            value.Append(text[i]);
            i++;
        }

        return false;
    }

    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';
}
