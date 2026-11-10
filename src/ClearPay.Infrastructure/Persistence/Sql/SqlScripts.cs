using System.Reflection;
using System.Text.RegularExpressions;

namespace ClearPay.Infrastructure.Persistence.Sql;

/// <summary>Embedded T-SQL scripts used by migrations, split into batches on <c>GO</c> lines.</summary>
public static partial class SqlScripts
{
    public static IReadOnlyList<string> Batches(string fileName)
    {
        var assembly = typeof(SqlScripts).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return GoLine().Split(reader.ReadToEnd())
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !b.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0))
            .ToList();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
