using System.Globalization;
using ClearPay.Application.Ports;
using Microsoft.AspNetCore.Mvc;

namespace ClearPay.Web.Controllers;

/// <summary>
/// Demo control for the fake PSPs: drop a business date's settlement files now instead of waiting for
/// the 01:00 UTC job. Reconciliation itself runs in the separate Operations service.
/// </summary>
[ApiController]
[Route("api/admin/psp")]
[JwtApi("Admin")]
public sealed class PspSimulatorAdminController : ControllerBase
{
    private readonly IPspSettlementFileWriter _writer;

    public PspSimulatorAdminController(IPspSettlementFileWriter writer)
    {
        _writer = writer;
    }

    /// <summary>POST /api/admin/psp/settlement-files?date=2026-11-08 (default: yesterday UTC).</summary>
    [HttpPost("settlement-files")]
    public async Task<IActionResult> Write([FromQuery] string? date, CancellationToken cancellationToken)
    {
        DateOnly businessDate;
        if (string.IsNullOrWhiteSpace(date))
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out businessDate))
            return Problem(title: "Invalid date", detail: "Use yyyy-MM-dd.", statusCode: StatusCodes.Status400BadRequest);

        var files = await _writer.WriteAsync(businessDate, cancellationToken).ConfigureAwait(false);
        return Ok(new { businessDate = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), files = files.Select(Path.GetFileName) });
    }
}
