using System.Text.RegularExpressions;

namespace ClearPay.Operations.Data.Sql;

/// <summary>Embedded T-SQL for migrations, split into batches on GO lines.</summary>
public static class OpsSql
{
    public static IReadOnlyList<string> Batches(string fileName)
    {
        var assembly = typeof(OpsSql).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return Regex.Split(reader.ReadToEnd(), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(b => b.Trim())
            .Where(b => b.Contains("CREATE", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
