using ABC.DiscoveryCity.API.Models;
using ABC.DiscoveryCity.API.Services;
using ABC.PdfProcessing.Syncfusion;
using Microsoft.AspNetCore.Mvc;

namespace ABC.DiscoveryCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MetadataController : ControllerBase
    {
        private readonly PdfMetadataService _pdfService;
        private readonly PdfMetaDataSyncFusionService _syncPdfService;
        public MetadataController(PdfMetadataService pdfService, PdfMetaDataSyncFusionService syncPdfService)
        {
            _pdfService = pdfService;
            _syncPdfService= syncPdfService;
        }

        [HttpPost("extract-abstract")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> ExtractAbstract([FromBody] PdfExtractRequest request)
        {
            try
            {
                if (request.PdfBytes == null || request.PdfBytes.Length == 0)
                {
                    return BadRequest("Empty PDF data.");
                }

                //var result = _pdfService.ExtractAbstract(request.PdfBytes, request.Folder ?? "", request.FileName ?? "");
                var result = _syncPdfService.ExtractAbstract(request.PdfBytes, request.Folder ?? "", request.FileName ?? "");
                return Ok(new { abstractText = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal error: {ex.Message}");
            }
        }
    }
}
