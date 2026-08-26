using System.Text;

namespace DbDelta.Core.Comparison;

// Collapses insignificant whitespace so reformatting a procedure does not register as a deployable
// change. Deliberately does not parse SQL: string literals keep their contents verbatim.
public static class SqlBodyNormalizer
{
    public static string Normalize(string body)
    {
        var result = new StringBuilder(body.Length);
        var inSingleQuote = false;
        var pendingSpace = false;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (inSingleQuote)
            {
                result.Append(c);
                if (c == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (c == '\'')
            {
                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }

                inSingleQuote = true;
                result.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    public static bool AreEquivalent(string left, string right, bool ignoreWhitespace) =>
        ignoreWhitespace
            ? string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal)
            : string.Equals(left, right, StringComparison.Ordinal);
}
