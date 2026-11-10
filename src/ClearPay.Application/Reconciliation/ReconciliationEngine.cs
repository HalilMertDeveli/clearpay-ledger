using System.Globalization;
using System.Text;

namespace ClearPay.Application.Reconciliation;

/// <summary>What our side believes happened at a PSP on a business date (captures and refunds only).</summary>
public sealed record LedgerSideRecord(
    Guid OperationId,
    string Provider,
    string Type,
    string Status,
    string? ProviderReference,
    Guid MerchantReference,
    decimal Amount);

/// <summary>One line of a PSP settlement file.</summary>
public sealed record SettlementLine(
    string Provider,
    DateOnly SettlementDate,
    string Type,
    string Reference,
    Guid MerchantReference,
    decimal Amount,
    string Currency);

public enum ReconciliationItemKind
{
    Matched = 0,

    /// <summary>We recorded a successful operation the PSP did not settle.</summary>
    MissingInSettlement = 1,

    /// <summary>The PSP settled something we have no successful record of.</summary>
    MissingInLedger = 2,

    /// <summary>Same reference, different amount.</summary>
    AmountMismatch = 3,

    /// <summary>Our operation is still Pending (PSP timeout) but the PSP settled it: it did happen.</summary>
    PendingConfirmedBySettlement = 4,

    /// <summary>Our operation is still Pending and the PSP did not settle it: it most likely never happened.</summary>
    PendingNotInSettlement = 5
}

public sealed record ReconciliationItemResult(
    ReconciliationItemKind Kind,
    string Provider,
    string Type,
    string? Reference,
    Guid? OperationId,
    decimal? LedgerAmount,
    decimal? SettlementAmount);

/// <summary>
/// Pure matching: by provider reference first, then (for operations whose PSP answer was lost
/// and therefore have no reference) by type + merchant reference. No I/O, fully unit-tested.
/// </summary>
public static class ReconciliationEngine
{
    public static IReadOnlyList<ReconciliationItemResult> Match(
        IReadOnlyCollection<LedgerSideRecord> ledger,
        IReadOnlyCollection<SettlementLine> settlement)
    {
        var results = new List<ReconciliationItemResult>();
        var unmatchedLines = settlement.ToList();

        foreach (var op in ledger.Where(o => o.ProviderReference is not null))
        {
            var line = unmatchedLines.FirstOrDefault(l =>
                l.Provider == op.Provider && string.Equals(l.Reference, op.ProviderReference, StringComparison.Ordinal));
            if (line is null)
            {
                results.Add(Item(op.Status == "Pending" ? ReconciliationItemKind.PendingNotInSettlement : ReconciliationItemKind.MissingInSettlement, op, null));
                continue;
            }

            unmatchedLines.Remove(line);
            results.Add(Item(line.Amount == op.Amount ? ReconciliationItemKind.Matched : ReconciliationItemKind.AmountMismatch, op, line));
        }

        foreach (var op in ledger.Where(o => o.ProviderReference is null))
        {
            var line = unmatchedLines.FirstOrDefault(l =>
                l.Provider == op.Provider && l.Type == op.Type && l.MerchantReference == op.MerchantReference);
            if (line is null)
            {
                results.Add(Item(ReconciliationItemKind.PendingNotInSettlement, op, null));
                continue;
            }

            unmatchedLines.Remove(line);
            results.Add(Item(line.Amount == op.Amount ? ReconciliationItemKind.PendingConfirmedBySettlement : ReconciliationItemKind.AmountMismatch, op, line));
        }

        results.AddRange(unmatchedLines.Select(l => new ReconciliationItemResult(
            ReconciliationItemKind.MissingInLedger, l.Provider, l.Type, l.Reference, null, null, l.Amount)));
        return results;
    }

    private static ReconciliationItemResult Item(ReconciliationItemKind kind, LedgerSideRecord op, SettlementLine? line) =>
        new(kind, op.Provider, op.Type, line?.Reference ?? op.ProviderReference, op.OperationId, op.Amount, line?.Amount);
}

/// <summary>
/// Settlement file format shared by the fake PSPs:
/// <c>provider,settlement_date,type,reference,merchant_ref,amount,currency</c> (invariant culture, header row).
/// </summary>
public static class SettlementCsv
{
    public const string Header = "provider,settlement_date,type,reference,merchant_ref,amount,currency";

    public static string Write(IEnumerable<SettlementLine> lines)
    {
        var sb = new StringBuilder().AppendLine(Header);
        foreach (var l in lines)
        {
            sb.Append(l.Provider).Append(',')
                .Append(l.SettlementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(l.Type).Append(',')
                .Append(l.Reference).Append(',')
                .Append(l.MerchantReference.ToString("N")).Append(',')
                .Append(l.Amount.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(l.Currency).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Parses a file; malformed lines are returned as errors instead of being silently skipped.</summary>
    public static (IReadOnlyList<SettlementLine> Lines, IReadOnlyList<string> Errors) Parse(string content)
    {
        var lines = new List<SettlementLine>();
        var errors = new List<string>();
        var rows = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows.Length == 0 || rows[0] != Header)
        {
            errors.Add("Missing or unexpected header.");
            return (lines, errors);
        }

        for (var i = 1; i < rows.Length; i++)
        {
            var f = rows[i].Split(',');
            if (f.Length != 7
                || !DateOnly.TryParseExact(f[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !Guid.TryParseExact(f[4], "N", out var merchantRef)
                || !decimal.TryParse(f[5], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                errors.Add($"Line {i + 1}: cannot parse '{rows[i]}'.");
                continue;
            }

            lines.Add(new SettlementLine(f[0], date, f[2], f[3], merchantRef, amount, f[6]));
        }

        return (lines, errors);
    }
}
