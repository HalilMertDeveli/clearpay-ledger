using System.Globalization;
using ClearPay.Application.Ports;
using Microsoft.AspNetCore.Mvc;

namespace ClearPay.Web.Controllers;

/// <summary>Admin view of the nightly reconciliation; a run can also be started by hand for any date.</summary>
[ApiController]
[Route("api/admin/reconciliation")]
[JwtApi("Admin")]
public sealed class ReconciliationAdminController : ControllerBase
{
    private readonly IReconciliationService _reconciliation;

    public ReconciliationAdminController(IReconciliationService reconciliation)
    {
        _reconciliation = reconciliation;
    }

    [HttpGet("runs")]
    public async Task<IActionResult> List([FromQuery] int take = 20, CancellationToken cancellationToken = default) =>
        Ok(await _reconciliation.ListAsync(take, cancellationToken).ConfigureAwait(false));

    [HttpGet("runs/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var found = await _reconciliation.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
            return NotFound();
        return Ok(new { run = found.Value.Run, items = found.Value.Items });
    }

    /// <summary>POST /api/admin/reconciliation/runs?date=2026-11-08 (default: yesterday UTC). Always runs (force).</summary>
    [HttpPost("runs")]
    public async Task<IActionResult> Run([FromQuery] string? date, CancellationToken cancellationToken)
    {
        DateOnly businessDate;
        if (string.IsNullOrWhiteSpace(date))
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out businessDate))
            return Problem(title: "Invalid date", detail: "Use yyyy-MM-dd.", statusCode: StatusCodes.Status400BadRequest);

        var summary = await _reconciliation.RunAsync(businessDate, force: true, cancellationToken).ConfigureAwait(false);
        return Created($"/api/admin/reconciliation/runs/{summary.RunId}", summary);
    }
}
