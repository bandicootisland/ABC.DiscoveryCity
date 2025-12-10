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
string csvBasePath = config["SyncSettings:CsvBasePath"] ?? @"H:\BookCity\Books\aa_derived_mirror_metadata_20251121\mariadb";
int defaultChunkSize = config.GetValue<int>("SyncSettings:DefaultChunkSize", 10000);
int maxRetries = config.GetValue<int>("SyncSettings:MaxRetries", 3);
int retryDelayMs = config.GetValue<int>("SyncSettings:RetryDelayMs", 5000);

Console.WriteLine($"Source: {MaskConnectionString(sourceConnectionString)}");
Console.WriteLine($"Target: {MaskConnectionString(targetConnectionString)}");
Console.WriteLine($"State:  {stateFile}");
Console.WriteLine($"CSV:    {csvBasePath}");
Console.WriteLine();

var syncState = new SyncState(stateFile);
var mover = new ChunkedDataMover(sourceConnectionString, targetConnectionString, syncState, maxRetries, retryDelayMs);
var csvLoader = new GzipCsvLoader(targetConnectionString, syncState, csvBasePath, maxRetries, retryDelayMs);
var fastLoader = new FastCsvLoader(targetConnectionString, syncState, csvBasePath, maxRetries, retryDelayMs);

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
    Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                     MAIN MENU                                ║");
    Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
    Console.WriteLine("║  1. Sync from Source MariaDB (libgenli tables)               ║");
    Console.WriteLine("║  2. Load from CSV/GZ files (original loader)                 ║");
    Console.WriteLine("║  3. Load from CSV/GZ files (FAST loader - schema-aware)      ║");
    Console.WriteLine($"║     [{csvBasePath}]");
    Console.WriteLine("║  Q. Quit                                                     ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    Console.Write("\nSelect option: ");
    
    var mainInput = Console.ReadLine()?.Trim().ToUpperInvariant();
    
    if (string.IsNullOrEmpty(mainInput) || mainInput == "Q") break;
    
    try
    {
        switch (mainInput)
        {
            case "1":
                await ShowMariaDBSyncMenu(mover, syncState, cts);
                break;
            case "2":
                await ShowCsvLoadMenu(csvLoader, syncState, cts, "Original");
                break;
            case "3":
                await ShowFastCsvLoadMenu(fastLoader, syncState, cts);
                break;
            default:
                Console.WriteLine("Invalid option.");
                break;
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Operation cancelled. Progress has been saved.");
        cts = new CancellationTokenSource();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nError: {ex.Message}");
        Console.WriteLine("Progress has been saved. You can resume later.");
    }
}

Console.WriteLine("\nGoodbye!");

// ============================================================================
// MariaDB Sync Menu
// ============================================================================
async Task ShowMariaDBSyncMenu(ChunkedDataMover mover, SyncState syncState, CancellationTokenSource cts)
{
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
    
    while (true)
    {
        Console.WriteLine("\nFetching table status...");
        var statuses = await mover.GetTableStatusAsync();
        var statusDict = statuses.ToDictionary(s => s.TableName, s => s);
        
        Console.WriteLine("\n┌──────────────────────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  MARIADB SYNC - libgenli Tables                                                          │");
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
        Console.WriteLine("│  A=Sync All | R=Reset | B=Back                                                           │");
        Console.WriteLine("│  Enter table number (1-9) to sync that table                                             │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────────────────┘");
        Console.Write("\nSelect option: ");
        
        var input = Console.ReadLine()?.Trim().ToUpperInvariant();
        
        if (string.IsNullOrEmpty(input) || input == "B") break;
        
        switch (input)
        {
            case "A":
                await SyncAllTablesAsync(mover, tableList, cts.Token);
                break;
            case "R":
                await ResetTableStateAsync(syncState, tableList);
                break;
            default:
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
        
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey(true);
    }
}

// ============================================================================
// CSV Load Menu (Original GzipCsvLoader)
// ============================================================================
async Task ShowCsvLoadMenu(GzipCsvLoader loader, SyncState syncState, CancellationTokenSource cts, string loaderName)
{
    // Scan files once (fast - no DB queries)
    Console.WriteLine($"\nScanning: {csvBasePath}");
    var configs = CsvFileConfig.ScanDirectory(csvBasePath);
    Console.WriteLine($"Found {configs.Count} tables.\n");
    
    while (true)
    {
        
        // Pagination - show 20 at a time
        int pageSize = 20;
        int currentPage = 0;
        int totalPages = (configs.Count + pageSize - 1) / pageSize;
        
        while (true)
        {
            Console.Clear();
            Console.WriteLine($"\n┌────────────────────────────────────────────────────────────────────────────────────────────────────┐");
            Console.WriteLine($"│  CSV LOADER ({loaderName}) - {configs.Count} tables found                                     Page {currentPage + 1}/{totalPages}        │");
            Console.WriteLine($"│  Path: {csvBasePath,-88} │");
            Console.WriteLine("├────┬────────────────────────────────────────────────┬──────────┬───────────────┬────────────────────┤");
            Console.WriteLine("│ #  │ Table Name                                     │ Parts    │ Size (MB)     │ Loaded             │");
            Console.WriteLine("├────┼────────────────────────────────────────────────┼──────────┼───────────────┼────────────────────┤");
            
            int startIdx = currentPage * pageSize;
            int endIdx = Math.Min(startIdx + pageSize, configs.Count);
            
            for (int i = startIdx; i < endIdx; i++)
            {
                var cfg = configs[i];
                
                // Fast: get file size from disk (no DB query)
                double sizeMB = cfg.DataFiles.Sum(f => File.Exists(f) ? new FileInfo(f).Length / 1024.0 / 1024.0 : 0);
                string parts = cfg.DataFiles.Count > 1 ? $"{cfg.DataFiles.Count} files" : "1 file";
                string size = sizeMB > 0 ? $"{sizeMB:F1}" : "-";
                
                // Get sync progress from state file (fast, no DB)
                var progress = syncState.GetProgress(cfg.TableName);
                string loaded = progress.TotalRowsSynced > 0 
                    ? $"{progress.TotalRowsSynced:N0} ({progress.Status})" 
                    : "-";
                
                // Truncate long table names
                string tableName = cfg.TableName.Length > 46 ? cfg.TableName.Substring(0, 43) + "..." : cfg.TableName;
                
                Console.WriteLine($"│ {i + 1,-2} │ {tableName,-46} │ {parts,-8} │ {size,-13} │ {loaded,-18} │");
            }
            
            Console.WriteLine("├────┴────────────────────────────────────────────────┴──────────┴───────────────┴────────────────────┤");
            Console.WriteLine("│  Commands:  N/PgDn=Next  P/PgUp=Prev  R=Reset table  T=Check target DB rows  B=Back                │");
            Console.WriteLine("├─────────────────────────────────────────────────────────────────────────────────────────────────────┤");
            Console.WriteLine("│  Load:  Enter number (e.g. 5) or range (e.g. 1-5) to start loading                                  │");
            Console.WriteLine("└─────────────────────────────────────────────────────────────────────────────────────────────────────┘");
            Console.Write("\nSelect option: ");
            
            var input = Console.ReadLine()?.Trim().ToUpperInvariant();
            
            if (string.IsNullOrEmpty(input) || input == "B") return;
            
            if ((input == "N" || input == "PAGEDOWN" || input == "PGDN") && currentPage < totalPages - 1)
            {
                currentPage++;
                continue;
            }
            else if ((input == "P" || input == "PAGEUP" || input == "PGUP") && currentPage > 0)
            {
                currentPage--;
                continue;
            }
            else if (input == "T")
            {
                // Query target DB for row counts (slow but more informative)
                Console.WriteLine("\nQuerying target database for row counts...");
                var statuses = await loader.GetFileStatusAsync();
                foreach (var st in statuses.Where(s => s.TargetRows > 0))
                {
                    Console.WriteLine($"  {st.TableName}: {st.TargetRows:N0} rows");
                }
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey(true);
                continue;
            }
            else if (input == "R")
            {
                Console.Write("Enter table number to reset: ");
                var resetInput = Console.ReadLine()?.Trim();
                if (int.TryParse(resetInput, out int resetNum) && resetNum >= 1 && resetNum <= configs.Count)
                {
                    syncState.Reset(configs[resetNum - 1].TableName);
                    Console.WriteLine($"Reset state for {configs[resetNum - 1].TableName}");
                }
                continue;
            }
            else if (input.Contains('-'))
            {
                // Range: 1-5
                var parts = input.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
                {
                    start = Math.Max(1, start);
                    end = Math.Min(configs.Count, end);
                    Console.WriteLine($"\nLoading tables {start} to {end}...");
                    for (int i = start; i <= end && !cts.Token.IsCancellationRequested; i++)
                    {
                        await LoadCsvTableAsync(loader, configs[i - 1], cts.Token);
                    }
                }
            }
            else if (int.TryParse(input, out int tableNum) && tableNum >= 1 && tableNum <= configs.Count)
            {
                await LoadCsvTableAsync(loader, configs[tableNum - 1], cts.Token);
            }
            else
            {
                Console.WriteLine("Invalid option.");
            }
            
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
        }
    }
}

async Task LoadCsvTableAsync(GzipCsvLoader loader, CsvFileConfig config, CancellationToken ct)
{
    Console.WriteLine($"\nStarting load for {config.TableName}...");
    Console.WriteLine("Press Ctrl+C to cancel (progress will be saved).\n");
    await loader.LoadFileAsync(config, ct);
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
        state.Reset(tables[num - 1]);
        Console.WriteLine($"Reset sync state for {tables[num - 1]}. Next sync will start from line 0.");
    }
    
    return Task.CompletedTask;
}

string MaskConnectionString(string connStr)
{
    return System.Text.RegularExpressions.Regex.Replace(
        connStr, 
        @"Password=([^;]+)", 
        "Password=***");
}

// ============================================================================
// Fast CSV Load Menu (FastCsvLoader - schema-aware, handles embedded newlines)
// ============================================================================
async Task ShowFastCsvLoadMenu(FastCsvLoader loader, SyncState syncState, CancellationTokenSource cts)
{
    // Scan files
    Console.WriteLine($"\nScanning: {csvBasePath}");
    var configs = CsvFileConfig.ScanDirectory(csvBasePath);
    Console.WriteLine($"Found {configs.Count} tables.\n");
    
    int pageSize = 20;
    int currentPage = 0;
    int totalPages = (configs.Count + pageSize - 1) / pageSize;
    
    while (true)
    {
        Console.Clear();
        Console.WriteLine($"\n┌────────────────────────────────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine($"│  FAST CSV LOADER (schema-aware) - {configs.Count} tables                                  Page {currentPage + 1}/{totalPages}        │");
        Console.WriteLine($"│  Path: {csvBasePath,-88} │");
        Console.WriteLine("├────┬────────────────────────────────────────────────┬──────────┬───────────────┬────────────────────┤");
        Console.WriteLine("│ #  │ Table Name                                     │ Parts    │ Size (MB)     │ Loaded             │");
        Console.WriteLine("├────┼────────────────────────────────────────────────┼──────────┼───────────────┼────────────────────┤");
        
        int startIdx = currentPage * pageSize;
        int endIdx = Math.Min(startIdx + pageSize, configs.Count);
        
        for (int i = startIdx; i < endIdx; i++)
        {
            var cfg = configs[i];
            double sizeMB = cfg.DataFiles.Sum(f => File.Exists(f) ? new FileInfo(f).Length / 1024.0 / 1024.0 : 0);
            string parts = cfg.DataFiles.Count > 1 ? $"{cfg.DataFiles.Count} files" : "1 file";
            string size = sizeMB > 0 ? $"{sizeMB:F1}" : "-";
            
            var progress = syncState.GetProgress(cfg.TableName);
            string loaded = progress.TotalRowsSynced > 0 
                ? $"{progress.TotalRowsSynced:N0} ({progress.Status})" 
                : "-";
            
            string tableName = cfg.TableName.Length > 46 ? cfg.TableName.Substring(0, 43) + "..." : cfg.TableName;
            Console.WriteLine($"│ {i + 1,-2} │ {tableName,-46} │ {parts,-8} │ {size,-13} │ {loaded,-18} │");
        }
        
        Console.WriteLine("├────┴────────────────────────────────────────────────┴──────────┴───────────────┴────────────────────┤");
        Console.WriteLine("│  N/PgDn=Next  P/PgUp=Prev  R=Reset table  B=Back                                                    │");
        Console.WriteLine("│  Enter number (e.g. 5) or range (e.g. 1-5) to load                                                  │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────────────────────────────────────────────┘");
        Console.Write("\nSelect option: ");
        
        var input = Console.ReadLine()?.Trim().ToUpperInvariant();
        
        if (string.IsNullOrEmpty(input) || input == "B") return;
        
        if ((input == "N" || input == "PAGEDOWN" || input == "PGDN") && currentPage < totalPages - 1)
        {
            currentPage++;
            continue;
        }
        else if ((input == "P" || input == "PAGEUP" || input == "PGUP") && currentPage > 0)
        {
            currentPage--;
            continue;
        }
        else if (input == "R")
        {
            Console.Write("Enter table number to reset: ");
            var resetInput = Console.ReadLine()?.Trim();
            if (int.TryParse(resetInput, out int resetNum) && resetNum >= 1 && resetNum <= configs.Count)
            {
                syncState.Reset(configs[resetNum - 1].TableName);
                Console.WriteLine($"Reset state for {configs[resetNum - 1].TableName}");
            }
            continue;
        }
        else if (input.Contains('-'))
        {
            var parts = input.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
            {
                start = Math.Max(1, start);
                end = Math.Min(configs.Count, end);
                Console.WriteLine($"\nLoading tables {start} to {end} with FastCsvLoader...");
                for (int i = start; i <= end && !cts.Token.IsCancellationRequested; i++)
                {
                    Console.WriteLine($"\nStarting load for {configs[i - 1].TableName}...");
                    await loader.LoadTableAsync(configs[i - 1], cts.Token);
                }
            }
        }
        else if (int.TryParse(input, out int tableNum) && tableNum >= 1 && tableNum <= configs.Count)
        {
            Console.WriteLine($"\nStarting load for {configs[tableNum - 1].TableName} with FastCsvLoader...");
            await loader.LoadTableAsync(configs[tableNum - 1], cts.Token);
        }
        else
        {
            Console.WriteLine("Invalid option.");
        }
        
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey(true);
    }
}
