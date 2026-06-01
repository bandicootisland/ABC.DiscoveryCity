using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Words.Common.Research;
using Microsoft.AspNetCore.Mvc;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ResearchController : ControllerBase
{
    private readonly DbService _dbService;

    public ResearchController(DbService dbService)
    {
        _dbService = dbService;
    }

    [HttpPost("init-schema")]
    public IActionResult InitSchema()
    {
        _dbService.InitResearchGraphTables();
        return Ok(new { ok = true });
    }

    [HttpPost("scan-names/document/{documentId:guid}")]
    public IActionResult ScanDocumentNames(Guid documentId, [FromBody] ResearchDocumentScanRequest? request = null)
    {
        try
        {
            var options = new ResearchDocumentScanOptions
            {
                Persist = request?.Persist ?? true,
                IncludeCoMentionRelationships = request?.IncludeCoMentionRelationships ?? true,
                SourceKind = string.IsNullOrWhiteSpace(request?.SourceKind) ? "viewer_scan" : request.SourceKind!,
                SourceRef = request?.SourceRef,
                ResearcherDisplayName = request?.ResearcherDisplayName,
                MaxRelationshipNamesPerSentence = request?.MaxRelationshipNamesPerSentence ?? 6,
                MaxCharacters = request?.MaxCharacters ?? 0,
                MinimumCandidateConfidence = request?.MinimumCandidateConfidence ?? 0.0m
            };

            return Ok(_dbService.ScanDocumentForResearchNames(documentId, options));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("scan-names/document/{documentId:guid}")]
    public IActionResult GetDocumentNames(Guid documentId)
    {
        try
        {
            return Ok(_dbService.GetPersistedResearchNames(documentId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("scan-names/path")]
    public IActionResult ScanDocumentNamesByPath([FromBody] ResearchDocumentPathScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new { error = "FilePath is required." });

        try
        {
            var options = new ResearchDocumentScanOptions
            {
                Persist = request.Persist,
                IncludeCoMentionRelationships = request.IncludeCoMentionRelationships,
                SourceKind = string.IsNullOrWhiteSpace(request.SourceKind) ? "viewer_scan" : request.SourceKind!,
                SourceRef = request.SourceRef ?? request.FilePath,
                ResearcherDisplayName = request.ResearcherDisplayName,
                MaxRelationshipNamesPerSentence = request.MaxRelationshipNamesPerSentence,
                MaxCharacters = request.MaxCharacters,
                MinimumCandidateConfidence = request.MinimumCandidateConfidence
            };

            return Ok(_dbService.ScanDocumentForResearchNamesByFilePath(request.FilePath, options));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("scan-names/path")]
    public IActionResult GetDocumentNamesByPath([FromQuery] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest(new { error = "FilePath is required." });

        try
        {
            return Ok(_dbService.GetPersistedResearchNamesByFilePath(filePath));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("name-research/document/{documentId:guid}")]
    public IActionResult GetNameResearch(Guid documentId)
    {
        try
        {
            return Ok(_dbService.GetNameResearchView(documentId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("name-research/path")]
    public IActionResult GetNameResearchByPath([FromQuery] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest(new { error = "FilePath is required." });

        try
        {
            return Ok(_dbService.GetNameResearchViewByFilePath(filePath));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("people/{entityId:guid}/aliases")]
    public IActionResult AddPersonAlias(Guid entityId, [FromBody] ResearchAliasRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
            return BadRequest(new { error = "Alias is required." });

        try
        {
            return Ok(_dbService.AddResearchPersonAlias(
                entityId,
                request.Alias,
                request.IsPrimary
                    ? "name"
                    : string.IsNullOrWhiteSpace(request.AliasType) ? "Name Link" : request.AliasType,
                request.IsPrimary,
                request.Confidence,
                string.IsNullOrWhiteSpace(request.SourceKind) ? "researcher" : request.SourceKind));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("people")]
    public IActionResult GetPeople([FromQuery] string? search = null, [FromQuery] int take = 250)
    {
        return Ok(_dbService.GetResearchPeopleAdmin(search, take));
    }

    [HttpGet("people/{entityId:guid}")]
    public IActionResult GetPerson(Guid entityId)
    {
        var person = _dbService.GetResearchPersonAdmin(entityId);
        return person == null
            ? NotFound(new { error = $"Name not found: {entityId}" })
            : Ok(person);
    }

    [HttpPost("people")]
    public IActionResult CreatePerson([FromBody] ResearchPersonCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest(new { error = "DisplayName is required." });

        try
        {
            return Ok(_dbService.CreateResearchPerson(
                request.DisplayName,
                request.Notes,
                string.IsNullOrWhiteSpace(request.Status) ? "verified" : request.Status,
                request.Confidence,
                string.IsNullOrWhiteSpace(request.SourceKind) ? "researcher" : request.SourceKind));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("people/{entityId:guid}")]
    public IActionResult UpdatePerson(Guid entityId, [FromBody] ResearchPersonUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest(new { error = "DisplayName is required." });

        try
        {
            return Ok(_dbService.UpdateResearchPerson(
                entityId,
                request.DisplayName,
                string.IsNullOrWhiteSpace(request.Status) ? "candidate" : request.Status,
                request.Notes));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("people/{sourceEntityId:guid}/merge")]
    public IActionResult MergePerson(Guid sourceEntityId, [FromBody] ResearchPersonMergeRequest request)
    {
        if (request.TargetEntityId == Guid.Empty)
            return BadRequest(new { error = "TargetEntityId is required." });

        try
        {
            return Ok(_dbService.MergeResearchPeople(
                sourceEntityId,
                request.TargetEntityId,
                string.IsNullOrWhiteSpace(request.SourceKind) ? "researcher" : request.SourceKind));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("people/{sourceEntityId:guid}/links")]
    public IActionResult LinkPerson(Guid sourceEntityId, [FromBody] ResearchNameLinkRequest request)
    {
        if (request.TargetEntityId == Guid.Empty)
            return BadRequest(new { error = "TargetEntityId is required." });

        try
        {
            return Ok(_dbService.AddResearchNameLink(
                sourceEntityId,
                request.TargetEntityId,
                request.Confidence,
                string.IsNullOrWhiteSpace(request.SourceKind) ? "researcher" : request.SourceKind));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("scan-names/text")]
    public IActionResult ScanTextNames([FromBody] ResearchTextScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Text is required." });

        return Ok(ResearchNameScanner.ScanText(request.Text, request.MaxCharacters));
    }
}

public class ResearchDocumentScanRequest
{
    public bool Persist { get; set; } = true;
    public bool IncludeCoMentionRelationships { get; set; } = true;
    public string? SourceKind { get; set; }
    public string? SourceRef { get; set; }
    public string? ResearcherDisplayName { get; set; }
    public int MaxRelationshipNamesPerSentence { get; set; } = 6;
    public int MaxCharacters { get; set; }
    public decimal MinimumCandidateConfidence { get; set; } = 0.0m;
}

public sealed class ResearchDocumentPathScanRequest : ResearchDocumentScanRequest
{
    public string FilePath { get; set; } = "";
}

public sealed class ResearchTextScanRequest
{
    public string Text { get; set; } = "";
    public int MaxCharacters { get; set; }
}

public sealed class ResearchAliasRequest
{
    public string Alias { get; set; } = "";
    public string AliasType { get; set; } = "";
    public bool IsPrimary { get; set; }
    public decimal Confidence { get; set; } = 1.0m;
    public string SourceKind { get; set; } = "researcher";
}

public sealed class ResearchPersonCreateRequest
{
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "verified";
    public decimal Confidence { get; set; } = 1.0m;
    public string? Notes { get; set; }
    public string SourceKind { get; set; } = "researcher";
}

public sealed class ResearchPersonUpdateRequest
{
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "candidate";
    public string? Notes { get; set; }
}

public sealed class ResearchPersonMergeRequest
{
    public Guid TargetEntityId { get; set; }
    public string SourceKind { get; set; } = "researcher";
}

public sealed class ResearchNameLinkRequest
{
    public Guid TargetEntityId { get; set; }
    public decimal Confidence { get; set; } = 1.0m;
    public string SourceKind { get; set; } = "researcher";
}
