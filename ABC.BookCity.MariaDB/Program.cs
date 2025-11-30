using ABC.BookCity.MariaDB;
using Microsoft.Extensions.Configuration;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║         ABC.BookCity MariaDB Sync Service                    ║");
Console.WriteLine("║         Chunked, Resumable, Idempotent Data Transfer         ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Build Configuration
var builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

IConfiguration config = builder.Build();

string sourceConnectionString = config.GetConnectionString("Source") 
    ?? "Server=localhost;Port=3307;User=allthethings;Password=password;Database=libgen_new;";
string targetConnectionString = config.GetConnectionString("Target") 
    ?? "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

string stateFile = config["SyncSettings:StateFile"] ?? @"H:\_DOCKER-DATA\sync_state.json";
int defaultChunkSize = config.GetValue<int>("SyncSettings:DefaultChunkSize", 10000);
int maxRetries = config.GetValue<int>("SyncSettings:MaxRetries", 3);
int retryDelayMs = config.GetValue<int>("SyncSettings:RetryDelayMs", 5000);

Console.WriteLine($"Source: {MaskConnectionString(sourceConnectionString)}");
Console.WriteLine($"Target: {MaskConnectionString(targetConnectionString)}");
Console.WriteLine($"State:  {stateFile}");
Console.WriteLine();

var syncState = new SyncState(stateFile);
var mover = new ChunkedDataMover(sourceConnectionString, targetConnectionString, syncState, maxRetries, retryDelayMs);

// Table list for numbered access
var tableList = new[]
{
    "libgenli_files",
    "libgenli_editions",
    "libgenli_editions_to_files",
    "libgenli_files_add_descr",
    "libgenli_editions_add_descr",
    "libgenli_series",
    "libgenli_series_add_descr",
    "libgenli_publishers",
    "libgenli_elem_descr"
};

// Cancellation support
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n\nCancellation requested. Finishing current chunk...");
    cts.Cancel();
};

while (true)
{
    // Fetch live status for menu display
    Console.WriteLine("\nFetching table status...");
    var statuses = await mover.GetTableStatusAsync();
    var statusDict = statuses.ToDictionary(s => s.TableName, s => s);
    
    Console.WriteLine("\n┌──────────────────────────────────────────────────────────────────────────────────────────┐");
    Console.WriteLine("│  TABLES                                                                                  │");
    Console.WriteLine("├────┬─────────────────────────────────┬───────────────┬───────────────┬───────────────────┤");
    Console.WriteLine("│ #  │ Table Name                      │ Source Rows   │ Target Rows   │ Sync %            │");
    Console.WriteLine("├────┼─────────────────────────────────┼───────────────┼───────────────┼───────────────────┤");
    
    for (int i = 0; i < tableList.Length; i++)
    {
        var tbl = tableList[i];
        var st = statusDict.GetValueOrDefault(tbl);
        string srcRows = st?.SourceRows >= 0 ? $"{st.SourceRows:N0}" : "N/A";
        string tgtRows = st?.TargetRows >= 0 ? $"{st.TargetRows:N0}" : "N/A";
        string pct = st?.SourceRows > 0 ? $"{st.SyncPercentage:F1}%" : "-";
        Console.WriteLine($"│ {i + 1}  │ {tbl,-31} │ {srcRows,-13} │ {tgtRows,-13} │ {pct,-17} │");
    }
    
    Console.WriteLine("├────┴─────────────────────────────────┴───────────────┴───────────────┴───────────────────┤");
    Console.WriteLine("│  COMMANDS: S=Status Detail | A=Sync All | R=Reset | Q=Quit                              │");
    Console.WriteLine("│  Enter table number (1-9) to sync that table                                            │");
    Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────────────────┘");
    Console.Write("\nSelect option: ");
    
    var input = Console.ReadLine()?.Trim().ToUpperInvariant();
    
    if (string.IsNullOrEmpty(input) || input == "Q") break;
    
    try
    {
        switch (input)
        {
            case "S":
                await ShowStatusDetailAsync(mover);
                break;
                
            case "A":
                await SyncAllTablesAsync(mover, tableList, cts.Token);
                break;
                
            case "R":
                await ResetTableStateAsync(syncState, tableList);
                break;
                
            default:
                // Try to parse as table number
                if (int.TryParse(input, out int tableNum) && tableNum >= 1 && tableNum <= tableList.Length)
                {
                    await SyncTableByNameAsync(mover, tableList[tableNum - 1], cts.Token);
                }
                else
                {
                    Console.WriteLine("Invalid option.");
                }
                break;
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Operation cancelled. Progress has been saved.");
        cts = new CancellationTokenSource(); // Reset for next operation
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nError: {ex.Message}");
        Console.WriteLine("Progress has been saved. You can resume later.");
    }
    
    Console.WriteLine("\nPress any key to continue...");
    Console.ReadKey(true);
}

Console.WriteLine("\nGoodbye!");

// Helper methods
async Task ShowStatusDetailAsync(ChunkedDataMover mover)
{
    Console.WriteLine("\nFetching detailed table status...\n");
    var statuses = await mover.GetTableStatusAsync();
    
    Console.WriteLine($"{"#",-3} {"Table",-35} {"Source",-15} {"Target",-15} {"Synced",-10} {"Status",-12}");
    Console.WriteLine(new string('-', 95));
    
    int idx = 1;
    foreach (var s in statuses)
    {
        string sourceStr = s.SourceRows >= 0 ? $"{s.SourceRows:N0}" : "N/A";
        string targetStr = s.TargetRows >= 0 ? $"{s.TargetRows:N0}" : "N/A";
        string pctStr = s.SourceRows > 0 ? $"{s.SyncPercentage:F1}%" : "-";
        
        Console.WriteLine($"{idx,-3} {s.TableName,-35} {sourceStr,-15} {targetStr,-15} {pctStr,-10} {s.Status,-12}");
        idx++;
    }
}

async Task SyncTableByNameAsync(ChunkedDataMover mover, string tableName, CancellationToken ct)
{
    var config = TableSyncConfig.GetConfig(tableName);
    Console.WriteLine($"\nStarting sync for {tableName}...");
    Console.WriteLine("Press Ctrl+C to cancel (progress will be saved).\n");
    await mover.SyncTableAsync(config, ct);
}

async Task SyncAllTablesAsync(ChunkedDataMover mover, string[] tables, CancellationToken ct)
{
    // Sync in order of dependencies / size (smaller first)
    var orderedTables = new[]
    {
        "libgenli_publishers",
        "libgenli_elem_descr",
        "libgenli_series",
        "libgenli_series_add_descr",
        "libgenli_files",
        "libgenli_files_add_descr",
        "libgenli_editions",
        "libgenli_editions_add_descr",
        "libgenli_editions_to_files"
    };
    
    foreach (var table in orderedTables)
    {
        if (ct.IsCancellationRequested) break;
        await SyncTableByNameAsync(mover, table, ct);
    }
}

Task ResetTableStateAsync(SyncState state, string[] tables)
{
    Console.WriteLine("\nSelect table to reset:");
    for (int i = 0; i < tables.Length; i++)
    {
        Console.WriteLine($"  {i + 1}. {tables[i]}");
    }
    Console.Write("\nEnter table number (or 0 to cancel): ");
    var input = Console.ReadLine()?.Trim();
    
    if (int.TryParse(input, out int num) && num >= 1 && num <= tables.Length)
    {
        var tableName = tables[num - 1];
        state.Reset(tableName);
        Console.WriteLine($"Reset sync state for {tableName}. Next sync will start from ID 0.");
    }
    else if (num != 0)
    {
        Console.WriteLine("Invalid selection.");
    }
    
    return Task.CompletedTask;
}

string MaskConnectionString(string connStr)
{
    // Simple masking of password
    return System.Text.RegularExpressions.Regex.Replace(
        connStr, 
        @"Password=([^;]+)", 
        "Password=***");
}
