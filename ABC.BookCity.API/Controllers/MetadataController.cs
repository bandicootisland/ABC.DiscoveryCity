using Microsoft.AspNetCore.Mvc;
using ABC.BookCity.API.Services;
using ABC.BookCity.API.Models;

namespace ABC.BookCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MetadataController : ControllerBase
    {
        private readonly PdfMetadataService _pdfService;

        public MetadataController(PdfMetadataService pdfService)
        {
            _pdfService = pdfService;
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

                var result = _pdfService.ExtractAbstract(request.PdfBytes, request.Folder ?? "", request.FileName ?? "");
                
                return Ok(new { abstractText = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal error: {ex.Message}");
            }
        }
    }
}
