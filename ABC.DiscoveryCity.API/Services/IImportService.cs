using ABC.DiscoveryCity.API.Models;

namespace ABC.DiscoveryCity.API.Services
{
    public interface IImportService
    {
        Task<List<DatasetInfo>> GetAvailableDatasetsAsync();
        Task RunDownloadAsync(string datasetName, Action<string> onLog);
        Task RunProcessAsync(string datasetName, Action<string> onLog);
        Task RunImportAsync(string datasetName, Action<string> onLog);
        Task<string> GetReadmeAsync();
        
        Task<string> SaveScriptAsync(DatasetInfo dataset);
        Task<string> GetScriptTemplateAsync(DatasetInfo dataset);
    }
}
