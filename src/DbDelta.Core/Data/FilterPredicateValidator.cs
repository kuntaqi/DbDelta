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
// being compared, an operator, a literal, a keyword from a short list, or a call to a function from an
// allowlist. A subquery, a second statement and a comment all still fail on the identifier rule. Whether
// what is left is well-formed is SQL Server's question, and it answers it with a better message than a
// hand-written parser would.
public static class FilterPredicateValidator
{
    private static readonly HashSet<string> Keywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "ESCAPE"
        };

    // Functions every argument of which is an ordinary expression. That is the boundary, and it is not an
    // arbitrary cut: DATEADD(day, -1, x), DATEPART(year, x) and CAST(x AS INT) each take a bare word that
    // is neither a column nor a literal, so admitting them would mean widening the alphabet to accept words
    // that are not columns — which is the one thing this check exists not to do. Anything needing that stays
    // out until the alphabet grows a category for it deliberately.
    //
    // Two hazards were weighed, and only one of them is about security.
    //
    // Reaching user code is already impossible, and not because of this list: a scalar user-defined function
    // in T-SQL must be schema-qualified, and this alphabet rejects a dot outside a number. SQL Server says
    // so itself — an unqualified name gets "is not a recognized built-in function name". So the list cannot
    // be escaped into a UDF, and it does not have to carry that weight alone.
    //
    // The hazard that *is* this tool's own is determinism. A predicate is embedded into two queries against
    // two databases on two connections at two moments. Anything that changes between calls, or reads
    // something outside the row, selects a different set of rows on each side — and the merge join reads
    // that as rows inserted and deleted. Every name below returns the same answer for the same input,
    // whenever and wherever it is asked.
    private static readonly HashSet<string> Functions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Text
            "LEN", "DATALENGTH", "LOWER", "UPPER", "LTRIM", "RTRIM", "TRIM",
            "LEFT", "RIGHT", "SUBSTRING", "REPLACE", "CHARINDEX", "PATINDEX",
            "CONCAT", "CONCAT_WS", "REVERSE", "STUFF", "REPLICATE", "SPACE",
            "ASCII", "UNICODE", "CHAR", "NCHAR",

            // Numbers
            "ABS", "CEILING", "FLOOR", "ROUND", "SIGN", "POWER", "SQRT", "SQUARE",
            "EXP", "LOG", "LOG10",

            // Dates, the parts of one. Nothing here reads the clock.
            "YEAR", "MONTH", "DAY",

            // Choosing between values
            "ISNULL", "COALESCE", "NULLIF", "IIF"
        };

    // Named individually so a refusal can say what is wrong rather than "not a column". Each of these
    // either changes between calls or answers differently depending on where it is asked — and the
    // predicate is asked twice, on two different databases.
    private static readonly Dictionary<string, string> Refused =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["GETDATE"] = "reads the clock",
            ["GETUTCDATE"] = "reads the clock",
            ["SYSDATETIME"] = "reads the clock",
            ["SYSUTCDATETIME"] = "reads the clock",
            ["SYSDATETIMEOFFSET"] = "reads the clock",
            ["CURRENT_TIMESTAMP"] = "reads the clock",
            ["NEWID"] = "returns a different value every call",
            ["NEWSEQUENTIALID"] = "returns a different value every call",
            ["RAND"] = "returns a different value every call",
            ["CHECKSUM"] = "is not guaranteed stable across collations or versions",
            ["FORMAT"] = "depends on the connection's culture, which the two sides need not share",
            ["DB_NAME"] = "answers with the database it is asked in, and the two sides are different databases",
            ["DB_ID"] = "answers with the database it is asked in, and the two sides are different databases",
            ["USER_NAME"] = "answers with the login asking, which the two sides need not share",
            ["SUSER_NAME"] = "answers with the login asking, which the two sides need not share",
            ["CURRENT_USER"] = "answers with the login asking, which the two sides need not share",
            ["SESSION_USER"] = "answers with the login asking, which the two sides need not share",
            ["SYSTEM_USER"] = "answers with the login asking, which the two sides need not share",
            ["HOST_NAME"] = "answers with the machine asking",
            ["APP_NAME"] = "answers with the application asking",
            ["OPENROWSET"] = "reads data from outside this database",
            ["OPENQUERY"] = "reads data from outside this database",
            ["OPENDATASOURCE"] = "reads data from outside this database"
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

                // A column first. A table with a column called Year or Left is entitled to it, and reading
                // the word as a function instead would refuse a filter over a column that exists.
                if (Keywords.Contains(word) || known.Contains(word))
                {
                    i = end;
                    continue;
                }

                if (Refused.TryGetValue(word, out var because))
                {
                    return FilterValidation.Rejected(
                        $"'{word}' cannot be used in a filter because it {because}. The predicate is run "
                        + "once against each database, on separate connections, so anything that answers "
                        + "differently between the two makes the comparison read rows as inserted and "
                        + "deleted when nothing changed.");
                }

                // A name only counts as a function where one is being called. A bare LEN is an identifier
                // that is not a column, whatever else the word means elsewhere.
                var called = IsCall(predicate, end);

                if (Functions.Contains(word))
                {
                    if (!called)
                    {
                        return FilterValidation.Rejected(
                            $"'{word}' is a function, so it needs its argument in brackets — {word}(SomeColumn). "
                            + "On its own it reads as a column, and there is no column by that name.");
                    }

                    i = end;
                    continue;
                }

                return called ? UnknownFunction(word) : Unknown(word, columns);
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

    // Every function this accepts, for a message that can be acted on rather than argued with.
    public static IReadOnlyCollection<string> AllowedFunctions =>
        Functions.Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsCall(string predicate, int after)
    {
        for (var i = after; i < predicate.Length; i++)
        {
            if (!char.IsWhiteSpace(predicate[i]))
            {
                return predicate[i] == '(';
            }
        }

        return false;
    }

    private static FilterValidation UnknownFunction(string word) =>
        FilterValidation.Rejected(
            $"'{word}' is not a function a filter can use. The ones it can are: "
            + $"{string.Join(", ", AllowedFunctions)}. The list holds only functions that return the same "
            + "answer for the same input wherever they are asked, because the predicate runs once against "
            + "each database — and only functions whose arguments are ordinary expressions, which is why "
            + "DATEADD, DATEPART and CAST are absent: each takes a bare word that is not a column.");

    // Naming what is available turns "invalid column" into something actionable, and it is the most likely
    // mistake by far: a column that exists on one side only is not comparable and so is not on this list.
    private static FilterValidation Unknown(string word, IReadOnlyCollection<string> columns)
    {
        var available = string.Join(", ", columns.Order(StringComparer.OrdinalIgnoreCase).Take(12));
        var more = columns.Count > 12 ? $", and {columns.Count - 12} more" : string.Empty;

        return FilterValidation.Rejected(
            $"'{word}' is not a column of this table on both sides, and not a function a filter can call. "
            + $"A filter names columns, compares them to constants, and may call one of a short list of "
            + $"functions — subqueries and everything else stay out, because a compare has to stay a read. "
            + $"Available columns: {available}{more}.");
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
