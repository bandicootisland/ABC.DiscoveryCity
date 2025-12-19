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
                // Read the PDF from the request body
                using var ms = new MemoryStream();
                await Request.Body.CopyToAsync(ms);
                ms.Position = 0;

                if (ms.Length == 0)
                {
                    return BadRequest("Empty PDF stream.");
                }

                var result = _pdfService.ExtractAbstract(ms);
                
                return Ok(new { abstractText = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal error: {ex.Message}");
            }
        }
    }
}
