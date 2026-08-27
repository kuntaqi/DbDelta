using System.Text;

namespace DbDelta.Core.Data;

// The predicate is the one place where text a person typed becomes SQL this tool executes. Calling that
// "injection" would be the wrong frame — it is a single-user local tool and the user already holds the
// credentials to both databases. The guarantee at risk is a different one:
//
//   a compare is a read, on both sides, always.
//
// The source is never written to and a read-only target can never be applied to, but a predicate is run
// during a *compare*, before either guard is anywhere near. Nothing else in the tool could execute DDL
// against a production target; an unchecked predicate could.
//
// So the alphabet is checked and the grammar is not. Every identifier has to be a column of the table
// being compared, and everything else has to be a literal, an operator or a keyword from a short list.
// That is enough to make "read these columns and compare them to constants" the only thing expressible —
// a function call, a subquery, a second statement and a comment all fail on the identifier rule. Whether
// what is left is well-formed is SQL Server's question, and it answers it with a better message than a
// hand-written parser would.
public static class FilterPredicateValidator
{
    private static readonly HashSet<string> Keywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "ESCAPE"
        };

    // Arithmetic is allowed because it cannot do anything: no operator here has a side effect.
    private const string OperatorCharacters = "=<>!+-*/%";

    public static FilterValidation Validate(string? predicate, IReadOnlyCollection<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (string.IsNullOrWhiteSpace(predicate))
        {
            return FilterValidation.Rejected("A filter needs a predicate — the WHERE clause without the word WHERE.");
        }

        var known = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var depth = 0;

        for (var i = 0; i < predicate.Length;)
        {
            var c = predicate[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // A semicolon ends the predicate's statement and starts another, which is the whole thing this
            // check exists to prevent. Comments are refused for the same reason: they can hide one.
            if (c == ';')
            {
                return FilterValidation.Rejected(
                    "A filter is one expression. A semicolon would start a second statement, and a compare "
                    + "only ever reads.");
            }

            if (c == '-' && i + 1 < predicate.Length && predicate[i + 1] == '-')
            {
                return FilterValidation.Rejected("A filter cannot contain a comment.");
            }

            if (c == '/' && i + 1 < predicate.Length && predicate[i + 1] == '*')
            {
                return FilterValidation.Rejected("A filter cannot contain a comment.");
            }

            if (c == '\'' || (char.ToUpperInvariant(c) == 'N' && i + 1 < predicate.Length && predicate[i + 1] == '\''))
            {
                var start = c == '\'' ? i : i + 1;
                var end = StringEnd(predicate, start);

                if (end < 0)
                {
                    return FilterValidation.Rejected("A string in the filter is not closed.");
                }

                i = end + 1;
                continue;
            }

            if (c == '[')
            {
                var close = predicate.IndexOf(']', i + 1);
                if (close < 0)
                {
                    return FilterValidation.Rejected("A bracketed name in the filter is not closed.");
                }

                var name = predicate[(i + 1)..close];
                if (!known.Contains(name))
                {
                    return Unknown(name, columns);
                }

                i = close + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var end = i;
                while (end < predicate.Length && (char.IsLetterOrDigit(predicate[end]) || predicate[end] is '_'))
                {
                    end++;
                }

                var word = predicate[i..end];

                if (!Keywords.Contains(word) && !known.Contains(word))
                {
                    return Unknown(word, columns);
                }

                i = end;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < predicate.Length && char.IsDigit(predicate[i + 1])))
            {
                var end = i;
                while (end < predicate.Length && (char.IsDigit(predicate[end]) || predicate[end] is '.' or 'e' or 'E'))
                {
                    end++;
                }

                i = end;
                continue;
            }

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return FilterValidation.Rejected("The filter closes a bracket it never opened.");
                }

                i++;
                continue;
            }

            if (c == ',' || OperatorCharacters.Contains(c, StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            return FilterValidation.Rejected(
                $"'{c}' cannot appear in a filter. Columns, numbers, 'strings', comparisons and "
                + "AND / OR / NOT / IN / IS NULL / LIKE / BETWEEN are what a filter is made of.");
        }

        return depth != 0
            ? FilterValidation.Rejected("The filter leaves a bracket open.")
            : FilterValidation.Ok;
    }

    // Naming what is available turns "invalid column" into something actionable, and it is the most likely
    // mistake by far: a column that exists on one side only is not comparable and so is not on this list.
    private static FilterValidation Unknown(string word, IReadOnlyCollection<string> columns)
    {
        var available = string.Join(", ", columns.Order(StringComparer.OrdinalIgnoreCase).Take(12));
        var more = columns.Count > 12 ? $", and {columns.Count - 12} more" : string.Empty;

        return FilterValidation.Rejected(
            $"'{word}' is not a column of this table on both sides. A filter can only name columns and "
            + $"compare them to constants — functions and subqueries are not accepted, because a compare "
            + $"has to stay a read. Available: {available}{more}.");
    }

    private static int StringEnd(string predicate, int start)
    {
        var text = new StringBuilder();

        for (var i = start + 1; i < predicate.Length; i++)
        {
            if (predicate[i] != '\'')
            {
                text.Append(predicate[i]);
                continue;
            }

            // Two quotes in a row are one escaped quote, not the end of the string.
            if (i + 1 < predicate.Length && predicate[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }
}
