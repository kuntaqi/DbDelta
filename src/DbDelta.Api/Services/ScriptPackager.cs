using System.IO.Compression;
using System.Text;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

// What "download the script" means depends on whether the plan has staged rows. Without them the script
// is the whole artifact and goes out as one .sql. With them it is not self-contained — the rows live in
// data files the script reads — so the two travel together in a zip rather than the user being handed a
// script that silently loads nothing.
public static class ScriptPackager
{
    public static (byte[] Content, string FileName, string ContentType) Package(
        SyncScript script,
        string targetDatabase)
    {
        ArgumentNullException.ThrowIfNull(script);

        var sql = script.ToSql();

        if (!script.HasLoads)
        {
            return (Encoding.UTF8.GetBytes(sql), $"sync_{targetDatabase}.sql", "application/sql");
        }

        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, $"sync_{targetDatabase}.sql", Encoding.UTF8.GetBytes(sql));
            Write(archive, "README.txt", Encoding.UTF8.GetBytes(Readme(script)));

            foreach (var load in script.Loads)
            {
                Write(archive, load.DataFileName, Encoding.UTF8.GetBytes(Csv(load)));
            }
        }

        return (buffer.ToArray(), $"sync_{targetDatabase}.zip", "application/zip");
    }

    // The format BULK INSERT is told to expect in the script: comma separated, LF terminated, UTF-8. A
    // NULL is an empty unquoted field, which is what SQL Server reads back as NULL under FORMAT='CSV'.
    public static string Csv(BulkLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);

        var text = new StringBuilder();

        foreach (var row in load.Rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                if (row[i] is { } value)
                {
                    text.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string Readme(SyncScript script)
    {
        var text = new StringBuilder();

        text.AppendLine("This plan moves more rows than fit comfortably into a readable script, so those rows");
        text.AppendLine("travel in the data files beside it instead of as INSERT statements.");
        text.AppendLine();
        text.AppendLine("To run it by hand:");
        text.AppendLine();
        text.AppendLine("  1. Put the .dat files somewhere the SQL Server service account can read. BULK INSERT");
        text.AppendLine("     resolves its path on the server, not on the machine running the script, so a local");
        text.AppendLine("     path only works when the server is local.");
        text.AppendLine("  2. Edit the FROM clauses in the .sql to match where you put them.");
        text.AppendLine("  3. Run the .sql as one batch. The staging tables are temporary, so it has to be one");
        text.AppendLine("     session from start to finish.");
        text.AppendLine();
        text.AppendLine("Applying from DbDelta needs none of this: it streams the same rows over its own");
        text.AppendLine("connection, inside the same transaction as everything else.");
        text.AppendLine();

        foreach (var load in script.Loads)
        {
            text.AppendLine($"  {load.DataFileName}  {load.RowCount} row(s) for {load.Table}");
        }

        return text.ToString();
    }

    private static void Write(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
