using System.Text;

namespace DbDelta.Core.Comparison;

// Collapses insignificant whitespace so reformatting a procedure does not register as a deployable
// change. Deliberately does not parse SQL: string literals keep their contents verbatim.
//
// It does have to recognise comments, though, for one reason. A comment is prose, and English prose
// contains apostrophes — "don't", "the report's owner". Treated as a string literal, one of those turns
// the whole rest of the body verbatim, and whitespace stops collapsing where it matters most: SQL Server
// stores a CREATE OR ALTER by blanking out the OR ALTER, so what comes back is "CREATE   VIEW" with the
// gap still in it. An object this tool wrote itself then compared as Different, permanently.
public static class SqlBodyNormalizer
{
    private enum Region
    {
        Code,
        StringLiteral,
        LineComment,
        BlockComment
    }

    public static string Normalize(string body)
    {
        var result = new StringBuilder(body.Length);
        var region = Region.Code;
        var blockDepth = 0;
        var pendingSpace = false;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            switch (region)
            {
                case Region.StringLiteral:
                    result.Append(c);
                    if (c == '\'')
                    {
                        region = Region.Code;
                    }

                    continue;

                case Region.LineComment:
                    if (c == '\n')
                    {
                        region = Region.Code;
                        pendingSpace = result.Length > 0;
                        continue;
                    }

                    break;

                case Region.BlockComment:
                    if (c == '/' && i + 1 < body.Length && body[i + 1] == '*')
                    {
                        blockDepth++;
                    }
                    else if (c == '*' && i + 1 < body.Length && body[i + 1] == '/')
                    {
                        blockDepth--;
                    }

                    break;
            }

            if (region == Region.Code)
            {
                if (c == '\'')
                {
                    region = Region.StringLiteral;
                }
                else if (c == '-' && i + 1 < body.Length && body[i + 1] == '-')
                {
                    region = Region.LineComment;
                }
                else if (c == '/' && i + 1 < body.Length && body[i + 1] == '*')
                {
                    region = Region.BlockComment;
                    blockDepth = 1;
                }
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

            if (region == Region.BlockComment && blockDepth == 0)
            {
                region = Region.Code;
            }
        }

        return result.ToString();
    }

    public static bool AreEquivalent(string left, string right, bool ignoreWhitespace) =>
        ignoreWhitespace
            ? string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal)
            : string.Equals(left, right, StringComparison.Ordinal);
}
