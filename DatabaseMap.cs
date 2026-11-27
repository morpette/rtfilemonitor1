using System.IO;
using System.Text;

namespace RtFileMonitor;

internal static class DatabaseMap
{
    internal static int Write(string root, string destination)
    {
        var files = new DirectoryInfo(root).GetFiles("*.sqlite", new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            MatchCasing = MatchCasing.CaseInsensitive,
            MatchType = MatchType.Simple
        });
        var report = new StringBuilder();
        report.AppendLine("# SQLite database map").AppendLine();
        report.AppendLine($"Folder: {CodeSpan(Path.GetFullPath(root))}").AppendLine();
        report.AppendLine(files.Length == 0 ? "No .sqlite files found." : $"Database files: {files.Length}");

        foreach (var file in files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            report.AppendLine().AppendLine($"## {CodeSpan(file.Name)}").AppendLine();
            try
            {
                report.AppendLine($"Size: {file.Length} bytes").AppendLine();
                var objects = SqliteActivity.TablesAndViews(file.FullName).ToArray();
                if (objects.Length == 0)
                    report.AppendLine("No tables or views.");
                foreach (var (type, name, columns, error) in objects)
                {
                    report.AppendLine($"- {type}: {CodeSpan(name)}");
                    foreach (var column in columns)
                    {
                        string declaredType = column.DeclaredType.Length == 0
                            ? "no declared type" : CodeSpan(column.DeclaredType);
                        report.AppendLine($"  - {CodeSpan(column.Name)} ({declaredType})");
                    }
                    if (error is not null)
                        report.AppendLine($"  - Could not inspect columns. {CodeSpan(error)}");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report.AppendLine($"Could not inspect this file. {CodeSpan(error.Message)}");
            }
        }

        string temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, report.ToString());
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return files.Length;
    }

    private static string CodeSpan(string value)
    {
        value = value.ReplaceLineEndings(" ");
        string fence = "`";
        while (value.Contains(fence, StringComparison.Ordinal))
            fence += "`";
        return fence.Length == 1 && value == value.Trim() ? $"`{value}`" : $"{fence} {value} {fence}";
    }
}
