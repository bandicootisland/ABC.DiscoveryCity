using Microsoft.AspNetCore.Mvc;
using ABC.BookCity.API.Services;

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
        public async Task<IActionResult> ExtractAbstract()
        {
            try
            {
                // Read the PDF from the request body as bytes to avoid stream position issues
                using var ms = new MemoryStream();
                await Request.Body.CopyToAsync(ms);
                var bytes = ms.ToArray();

                if (bytes.Length == 0)
                {
                    return BadRequest("Empty PDF stream.");
                }

                var result = _pdfService.ExtractAbstract(bytes);
                
                return Ok(new { abstractText = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal error: {ex.Message}");
            }
        }
    }
}
