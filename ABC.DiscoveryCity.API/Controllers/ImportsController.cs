using ABC.DiscoveryCity.API.Models;
using ABC.DiscoveryCity.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ABC.DiscoveryCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ImportsController : ControllerBase
    {
        private readonly IImportService _importService;

        public ImportsController(IImportService importService)
        {
            _importService = importService;
        }

        [HttpGet]
        public async Task<ActionResult<List<DatasetInfo>>> GetDatasets()
        {
            return await _importService.GetAvailableDatasetsAsync();
        }

        [HttpGet("readme")]
        public async Task<ActionResult<string>> GetReadme()
        {
            return await _importService.GetReadmeAsync();
        }

        [HttpGet("run/{datasetName}/{actionType}")]
        public async Task RunAction(string datasetName, string actionType)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            
            Action<string> onLog = async (msg) => 
            {
                // Simple SSE format: data: message\n\n
                // We need to be careful with newlines in the message
                var lines = msg.Split('\n');
                foreach(var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var data = $"data: {line}\n\n";
                    await Response.WriteAsync(data);
                    await Response.Body.FlushAsync();
                }
            };

            try 
            {
                switch (actionType.ToLower())
                {
                    case "download":
                        await _importService.RunDownloadAsync(datasetName, onLog);
                        break;
                    case "process":
                        await _importService.RunProcessAsync(datasetName, onLog);
                        break;
                    case "import":
                        await _importService.RunImportAsync(datasetName, onLog);
                        break;
                    default:
                        onLog($"Unknown action: {actionType}");
                        break;
                }
                onLog("DONE");
            }
            catch (Exception ex)
            {
                onLog($"ERROR: {ex.Message}");
            }
        }

        [HttpPost("save-script")]
        public async Task<ActionResult<string>> SaveScript([FromBody] DatasetInfo dataset)
        {
            try
            {
                var path = await _importService.SaveScriptAsync(dataset);
                return Ok(path);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("template")]
        public async Task<ActionResult<string>> GetTemplate([FromBody] DatasetInfo dataset)
        {
            return await _importService.GetScriptTemplateAsync(dataset);
        }
    }
}
