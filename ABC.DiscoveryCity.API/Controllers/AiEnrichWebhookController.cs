using Microsoft.AspNetCore.Mvc;

namespace ABC.DiscoveryCity.API.Controllers;

/// <summary>
/// Inbound webhook from ABC.AI. ABC.AI completes enrichment of a PDF and POSTs
/// here so DiscoveryCity can pull the result into its tiered-search / user-edits
/// tables (or its Elasticsearch index).
///
/// Endpoint: <c>POST /api/webhooks/ai-enrich</c>
/// Headers : <c>Idempotency-Key</c> = the AI job id
/// Body    : <see cref="AiEnrichPayload"/> — see ABC.AI.Models.FeederReplyPayload
///
/// On receipt, the canonical result is already persisted in
/// <c>abc_ai.published_results</c> (PostgreSQL on this Mac, port 5433),
/// keyed by <c>(feeder, source_id, pdf_sha256)</c>.
/// Read either:
///   1. Direct SQL: <c>SELECT result_json FROM abc_ai.published_results WHERE ...</c>
///   2. HTTP:       <c>GET http://api.macmini.internal/ai/api/results/discoverycity/{sourceId}</c>
/// The JSON shape is versioned via the <c>SchemaVersion</c> field.
///
/// Phase 1 (this commit): receive + log + 204 — proves the contract works.
/// Phase 2 (TODO for the DiscoveryCity team):
///   - Fetch the result_json (DB or HTTP) and project per-page text into
///     the tiered-search index (Elasticsearch) plus the editorial-overrides
///     Postgres tables. Equations and figure captions are useful as separate
///     fielded data for search filters.
/// </summary>
[ApiController]
[Route("api/webhooks/ai-enrich")]
public class AiEnrichWebhookController : ControllerBase
{
    private readonly ILogger<AiEnrichWebhookController> _log;

    public AiEnrichWebhookController(ILogger<AiEnrichWebhookController> log)
    {
        _log = log;
    }

    /// <summary>
    /// Receives the enrichment-complete notification.
    /// Returns <c>204 No Content</c> on success.
    /// </summary>
    [HttpPost]
    public IActionResult Notify([FromBody] AiEnrichPayload? payload)
    {
        if (payload is null) return BadRequest(new { error = "missing body" });
        if (payload.JobId == Guid.Empty)        return BadRequest(new { error = "JobId required" });
        if (string.IsNullOrEmpty(payload.SourceId)) return BadRequest(new { error = "SourceId required" });
        if (string.IsNullOrEmpty(payload.Feeder))   return BadRequest(new { error = "Feeder required" });

        var idem = Request.Headers.TryGetValue("Idempotency-Key", out var k) ? k.ToString() : null;

        _log.LogInformation(
            "ai-enrich webhook: jobId={JobId} feeder={Feeder} sourceId={SourceId} status={Status} pages={Pages} sidecar={Sidecar} idempotencyKey={Idem}",
            payload.JobId, payload.Feeder, payload.SourceId, payload.Status,
            payload.PagesProcessed, payload.SidecarPath, idem);

        if (!string.IsNullOrEmpty(payload.Error))
            _log.LogWarning("ai-enrich webhook: jobId={JobId} reported error: {Error}",
                payload.JobId, payload.Error);

        // TODO Phase 2: pull result_json from abc_ai.published_results (or via
        // ABC.AI HTTP) and project into DiscoveryCity's tiered-search + editorial
        // overrides. See class docs.

        return NoContent();
    }

    /// <summary>
    /// Wire shape of ABC.AI's reply (matches ABC.AI.Models.FeederReplyPayload).
    /// Defined locally to avoid coupling DiscoveryCity to the ABC.AI assembly.
    /// </summary>
    public sealed record AiEnrichPayload(
        Guid JobId,
        string SourceId,
        string Feeder,
        string Status,                  // "done" | "failed"
        string SidecarPath,
        int PagesProcessed,
        IReadOnlyList<string> StagesCompleted,
        string? Error = null);
}
