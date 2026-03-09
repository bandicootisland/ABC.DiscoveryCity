namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;

public interface IIngestionStep
{
    string Name { get; }
    Task ExecuteAsync(IngestionContext ctx);
}
