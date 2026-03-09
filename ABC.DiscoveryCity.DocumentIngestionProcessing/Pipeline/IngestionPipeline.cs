using System.Diagnostics;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;

public class IngestionPipeline
{
    private readonly List<IIngestionStep> _steps = new();

    public IngestionPipeline AddStep(IIngestionStep step)
    {
        _steps.Add(step);
        return this;
    }

    public IngestionPipeline AddStep(IIngestionStep step, bool condition)
    {
        if (condition) _steps.Add(step);
        return this;
    }

    public async Task ExecuteAsync(IngestionContext ctx)
    {
        var sw = Stopwatch.StartNew();

        foreach (var step in _steps)
        {
            var stepSw = Stopwatch.StartNew();
            try
            {
                await step.ExecuteAsync(ctx);
                stepSw.Stop();

                if (stepSw.ElapsedMilliseconds > 500)
                    Console.WriteLine($"    [{step.Name}] {stepSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [{step.Name}] FAILED: {ex.Message}");
                throw;
            }
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds > 2000)
            Console.WriteLine($"  Total pipeline: {sw.ElapsedMilliseconds}ms");
    }
}
