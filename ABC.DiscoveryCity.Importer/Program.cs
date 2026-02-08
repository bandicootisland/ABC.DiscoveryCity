using ABC.DiscoveryCity.Importer;
using ABC.DiscoveryCity.MariaDB;
using Microsoft.Extensions.Configuration;

Console.WriteLine("ABC.DiscoveryCity Importer Service");
Console.WriteLine("-----------------------------");

// Build Configuration
var builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

IConfiguration config = builder.Build();

string connectionString = config.GetConnectionString("DefaultConnection") 
    ?? "Server=localhost;Port=3306;User=root;Password=password;AllowLoadLocalInfile=true;";

string baseFolder = config["ImportSettings:BaseFolder"] ?? @"H:\BookCity\Books";
string historyFile = config["ImportSettings:HistoryFile"] ?? @"H:\_DOCKER-DATA\import_history.json";
string sevenZipPath = config["ImportSettings:SevenZipPath"] ?? @"C:\Program Files\7-Zip\7z.exe";

// Update Static Connection String
SqlImporter.ConnectionString = connectionString;

var history = new ImportHistory(historyFile);

if (!Directory.Exists(baseFolder))
{
    Console.WriteLine($"Directory not found: {baseFolder}");
    Console.WriteLine("Please ensure the torrent has started downloading.");
    // Don't return, allow user to maybe change config or use 'L' option with absolute path
}

while (true)
{
    Console.WriteLine("\nAvailable files to import:");
    Console.WriteLine("0. [BATCH] Import Standard Schemas (Create DB + Tables)");
    Console.WriteLine("T. [TORRENT] Import from Completed Torrent Downloads");
    Console.WriteLine("F. [FILES] Scan and Catalog Book Files (bookcityfile table)");
    Console.WriteLine("D. [DATA] List all Data Files (.dat.gz)");    
    Console.WriteLine("V. [UTIL] Verify GZip Integrity of a file");
    Console.WriteLine("L. [LEGACY] Import from RAR Archive (Libgen)");
    Console.WriteLine("H. [HATHI] Import HathiTrust Records (TSV format)");
    Console.WriteLine("O. [OPENLIB] Import OpenLibrary Dump (ol_dump_latest.txt)");
    Console.WriteLine("W. [WORLDCAT] Import WorldCat Data (.dat.gz, Span-based)");
    Console.WriteLine("J. [WORLDCAT-JSON] Import WorldCat Full Records (JSONL.ZST)");
    Console.WriteLine("C. [CODES] Import aarecords_codes (377GB, Span-based)");
    
    List<string> schemaFiles = new List<string>();
    List<string> allDataFiles = new List<string>();

    if (Directory.Exists(baseFolder))
    {
        schemaFiles = Directory.GetFiles(baseFolder, "*.sql.gz")
                             .OrderBy(f => f)
                             .ToList();
        allDataFiles = Directory.GetFiles(baseFolder, "*.*.gz").ToList();
    }

    for (int i = 0; i < schemaFiles.Count; i++)
    {   
        var fileInfo = new FileInfo(schemaFiles[i]);
        var fileName = Path.GetFileName(schemaFiles[i]);
        
        // Check for associated data files
        string baseName = fileName.Replace("-schema.sql.gz", "");
        var matchingDataFiles = allDataFiles.Where(d => Path.GetFileName(d).StartsWith(baseName + ".")).ToList();
        var matchingDataCount = matchingDataFiles.Count;
        
        string status = history.IsImported(fileName) ? "[DONE] " : "";
        
        // Check if all data files are imported
        int importedDataCount = matchingDataFiles.Count(d => history.IsImported(Path.GetFileName(d)));
        string dataStatus = "";
        if (matchingDataCount > 0)
        {
             if (importedDataCount == matchingDataCount) dataStatus = " [ALL DATA IMPORTED]";
             else if (importedDataCount > 0) dataStatus = $" [Data: {importedDataCount}/{matchingDataCount} imported]";
        }

        string dataOption = matchingDataCount > 0 ? $" [Data: {i + 1}D ({matchingDataCount} files){dataStatus}]" : "";

        Console.WriteLine($"{i + 1}. {status}{fileName} ({fileInfo.Length / 1024.0:N0} KB){dataOption}");
    }

    Console.WriteLine("\nEnter the number of the file to import (e.g. '38' for schema, '38D' for data, or 'q' to quit):");
    var input = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "q") break;

    if (input.ToLower() == "v")
    {
        Console.WriteLine("\nEnter the full path or filename to verify:");
        var fileToVerify = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(fileToVerify))
        {
            string fullPath = fileToVerify;
            if (!Path.IsPathRooted(fileToVerify))
            {
                fullPath = Path.Combine(baseFolder, fileToVerify);
            }
            
            // Try to find by number if user entered a number
            if (int.TryParse(fileToVerify, out int vChoice))
            {
                 // Check schema list
                 if (vChoice > 0 && vChoice <= schemaFiles.Count) fullPath = schemaFiles[vChoice - 1];
            }
            // Try to find by number + D
            else if (fileToVerify.EndsWith("D", StringComparison.OrdinalIgnoreCase) && int.TryParse(fileToVerify.Substring(0, fileToVerify.Length - 1), out int dChoice))
            {
                 if (dChoice > 0 && dChoice <= schemaFiles.Count)
                 {
                    var sFile = schemaFiles[dChoice - 1];
                    string baseName = Path.GetFileName(sFile).Replace("-schema.sql.gz", "");
                    var dFiles = allDataFiles.Where(d => Path.GetFileName(d).StartsWith(baseName + ".")).ToList();
                    if (dFiles.Count > 0) fullPath = dFiles[0]; // Just verify the first one for now
                 }
            }

            await SqlImporter.VerifyGzipAsync(fullPath);
            Console.WriteLine("\nPress any key to return to the menu...");
            Console.ReadKey();
        }
        continue;
    }

    // Check for Data Link Request (e.g. "38D")
    bool isDataLinkRequest = input.EndsWith("D", StringComparison.OrdinalIgnoreCase) && input.Length > 1;

    if (input.ToLower() == "t")
    {
        await HandleTorrentDownloadsMenu(connectionString);
        continue;
    }

    if (input.ToLower() == "f")
    {
        await HandleFileCatalogMenu(connectionString);
        continue;
    }

    if (input.ToLower() == "l")
    {
        Console.WriteLine("\nEnter path to RAR archive (e.g. libgen.rar):");
        var rarPath = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(rarPath))
        {
            // Use configured connection string and 7z path
            var importer = new DataBatchingImporter(connectionString, sevenZipPath); 
            await importer.ImportArchiveAsync(rarPath);
        }
        continue;
    }
    
    if (input.ToLower() == "h")
    {
        Console.WriteLine("\n=== HathiTrust Import ===");
        Console.WriteLine("This imports HathiTrust metadata from TSV files (hathi_full.txt format).");
        Console.WriteLine("\nDefault path: H:\\BookCity\\Books\\hathi_full.txt\\hathifiles20251101-29-9iybh2");
        Console.WriteLine("\nEnter path to HathiTrust TSV file (or press Enter for default):");
        var hathiPath = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrWhiteSpace(hathiPath))
        {
            hathiPath = @"H:\BookCity\Books\hathi_full.txt\hathifiles20251101-29-9iybh2";
        }
        
        if (!File.Exists(hathiPath))
        {
            Console.WriteLine($"File not found: {hathiPath}");
            Console.WriteLine("\nPress any key to return to the menu...");
            Console.ReadKey();
            continue;
        }
        
        var fileInfo = new FileInfo(hathiPath);
        Console.WriteLine($"\nFile: {hathiPath}");
        Console.WriteLine($"Size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB");
        
        Console.WriteLine("\nSkip rows (for resuming, 0 = start from beginning):");
        var skipInput = Console.ReadLine()?.Trim();
        long skipRows = 0;
        if (!string.IsNullOrWhiteSpace(skipInput))
        {
            long.TryParse(skipInput, out skipRows);
        }
        
        Console.WriteLine($"\nWill import from row {skipRows:N0}. Continue? (Y/n):");
        var confirm = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(confirm) && confirm.ToLower().StartsWith("n"))
        {
            continue;
        }
        
        // First, run the schema creation
        Console.WriteLine("\nCreating HathiTrust database schema...");
        var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", 
            "DataImports", "hathitrust", "create_hathitrust_tables.sql");
        
        // If relative path doesn't work, try absolute
        if (!File.Exists(schemaPath))
        {
            schemaPath = @"H:\Developer.BookCity\ABC.DiscoveryCity\DataImports\hathitrust\create_hathitrust_tables.sql";
        }
        
        if (File.Exists(schemaPath))
        {
            var schemaSql = await File.ReadAllTextAsync(schemaPath);
            try
            {
                using var conn = new MySqlConnector.MySqlConnection(connectionString);
                await conn.OpenAsync();
                
                // Split by semicolon and execute each statement
                var statements = schemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var stmt in statements)
                {
                    var trimmed = stmt.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("--")) continue;
                    
                    try
                    {
                        using var cmd = new MySqlConnector.MySqlCommand(trimmed + ";", conn);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        // Ignore "already exists" errors
                        if (!ex.Message.Contains("already exists"))
                        {
                            Console.WriteLine($"Schema warning: {ex.Message}");
                        }
                    }
                }
                Console.WriteLine("Schema created/verified.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Schema error: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Schema file not found: {schemaPath}");
            Console.WriteLine("Continuing anyway (table may already exist)...");
        }
        
        // Now run the loader
        var loader = new ABC.DiscoveryCity.MariaDB.HathiTrustLoader(connectionString, hathiPath, batchSize: 5000);
        var (processed, inserted, skipped) = await loader.LoadAsync(skipRows);
        
        Console.WriteLine($"\nImport complete!");
        Console.WriteLine($"Processed: {processed:N0}");
        Console.WriteLine($"Inserted: {inserted:N0}");
        Console.WriteLine($"Skipped: {skipped:N0}");
        
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }
    
    if (input.ToLower() == "o")
    {
        Console.WriteLine("\n=== OpenLibrary Import (Span-based) ===");
        Console.WriteLine("This imports OpenLibrary dump from ol_dump_latest.txt");
        Console.WriteLine("Tables: ol_base, ol_authors, ol_works, ol_editions");
        Console.WriteLine("\nDefault path: H:\\BookCity\\Books\\ol_dump_latest.txt\\ol_dump_latest.txt");
        Console.WriteLine("\nEnter path to OpenLibrary TSV file (or press Enter for default):");
        var olPath = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrWhiteSpace(olPath))
        {
            olPath = @"H:\BookCity\Books\ol_dump_latest.txt\ol_dump_latest.txt";
        }
        
        if (!File.Exists(olPath))
        {
            Console.WriteLine($"File not found: {olPath}");
            Console.WriteLine("\nPress any key to return to the menu...");
            Console.ReadKey();
            continue;
        }
        
        var fileInfo = new FileInfo(olPath);
        Console.WriteLine($"\nFile: {olPath}");
        Console.WriteLine($"Size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB");
        
        // First, run the schema creation
        Console.WriteLine("\nCreating OpenLibrary database schema...");
        var olSchemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", 
            "DataImports", "openlibrary", "create_openlibrary_tables.sql");
        
        // If relative path doesn't work, try absolute
        if (!File.Exists(olSchemaPath))
        {
            olSchemaPath = @"H:\Developer.BookCity\ABC.DiscoveryCity\DataImports\openlibrary\create_openlibrary_tables.sql";
        }
        
        if (File.Exists(olSchemaPath))
        {
            var schemaSql = await File.ReadAllTextAsync(olSchemaPath);
            try
            {
                using var schemaConn = new MySqlConnector.MySqlConnection(connectionString);
                await schemaConn.OpenAsync();
                
                // Split by semicolon and execute each statement
                var statements = schemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var stmt in statements)
                {
                    var trimmed = stmt.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("--")) continue;
                    
                    try
                    {
                        using var cmd = new MySqlConnector.MySqlCommand(trimmed + ";", schemaConn);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        // Ignore "already exists" errors
                        if (!ex.Message.Contains("already exists"))
                        {
                            Console.WriteLine($"Schema warning: {ex.Message}");
                        }
                    }
                }
                Console.WriteLine("Schema created/verified.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Schema error: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Schema file not found: {olSchemaPath}");
            Console.WriteLine("Continuing anyway (tables may already exist)...");
        }
        
        // Check current row count
        try
        {
            using var checkConn = new MySqlConnector.MySqlConnection(connectionString);
            await checkConn.OpenAsync();
            using var checkCmd = new MySqlConnector.MySqlCommand("SELECT COUNT(*) FROM allthethings.ol_base", checkConn);
            var currentRows = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
            Console.WriteLine($"Current rows in ol_base: {currentRows:N0}");
        }
        catch { }
        
        Console.WriteLine("\nSkip rows (for resuming, 0 = start from beginning):");
        var skipInput = Console.ReadLine()?.Trim();
        long skipRows = 0;
        if (!string.IsNullOrWhiteSpace(skipInput))
        {
            long.TryParse(skipInput, out skipRows);
        }
        
        Console.WriteLine($"\nWill import from row {skipRows:N0}. Continue? (Y/n):");
        var confirm = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(confirm) && confirm.ToLower().StartsWith("n"))
        {
            continue;
        }
        
        // Run the Span-based loader (high-performance)
        var olLoader = new ABC.DiscoveryCity.MariaDB.OpenLibraryLoaderSpan(connectionString, olPath, batchSize: 2000);
        var (olProcessed, olInserted, olSkipped) = await olLoader.LoadAsync(skipRows);
        
        Console.WriteLine($"\nImport complete!");
        Console.WriteLine($"Processed: {olProcessed:N0}");
        Console.WriteLine($"Inserted: {olInserted:N0}");
        Console.WriteLine($"Skipped: {olSkipped:N0}");
        
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }
    
    if (input.ToLower() == "w")
    {
        Console.WriteLine("\n=== WorldCat Import (Span-based) ===");
        Console.WriteLine("This imports WorldCat data from .dat.gz file");
        Console.WriteLine("Table: allthethings.annas_archive_meta__aacid__worldcat");
        Console.WriteLine("Columns: aacid, primary_id, md5, byte_offset, byte_length");
        Console.WriteLine("\nDefault path: H:\\BookCity\\Books\\allthethings.annas_archive_meta__aacid__worldcat.00000.dat.gz");
        Console.WriteLine("\nEnter path to WorldCat .dat.gz file (or press Enter for default):");
        var wcPath = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrWhiteSpace(wcPath))
        {
            wcPath = @"H:\BookCity\Books\allthethings.annas_archive_meta__aacid__worldcat.00000.dat.gz";
        }
        
        if (!File.Exists(wcPath))
        {
            Console.WriteLine($"File not found: {wcPath}");
            Console.WriteLine("\nPress any key to return to the menu...");
            Console.ReadKey();
            continue;
        }
        
        var wcFileInfo = new FileInfo(wcPath);
        Console.WriteLine($"\nFile: {wcPath}");
        Console.WriteLine($"Size: {wcFileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB (compressed)");
        
        // Check current row count
        long currentRows = 0;
        try
        {
            using var checkConn = new MySqlConnector.MySqlConnection(connectionString);
            await checkConn.OpenAsync();
            using var checkCmd = new MySqlConnector.MySqlCommand(
                "SELECT COUNT(*) FROM allthethings.annas_archive_meta__aacid__worldcat", checkConn);
            currentRows = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
            Console.WriteLine($"Current rows in table: {currentRows:N0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not check row count: {ex.Message}");
        }
        
        Console.WriteLine("\nSkip rows (for resuming, 0 = start from beginning):");
        if (currentRows > 0)
        {
            Console.WriteLine($"(Suggestion: enter {currentRows} to resume from where you left off)");
        }
        var wcSkipInput = Console.ReadLine()?.Trim();
        long wcSkipRows = 0;
        if (!string.IsNullOrWhiteSpace(wcSkipInput))
        {
            long.TryParse(wcSkipInput, out wcSkipRows);
        }
        
        Console.WriteLine($"\nWill import from row {wcSkipRows:N0}. Continue? (Y/n):");
        var wcConfirm = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(wcConfirm) && wcConfirm.ToLower().StartsWith("n"))
        {
            continue;
        }
        
        // Run the loader
        var wcLoader = new ABC.DiscoveryCity.MariaDB.WorldCatLoader(connectionString, wcPath, batchSize: 5000);
        var (wcProcessed, wcInserted, wcSkipped) = await wcLoader.LoadAsync(wcSkipRows);
        
        Console.WriteLine($"\nImport complete!");
        Console.WriteLine($"Processed: {wcProcessed:N0}");
        Console.WriteLine($"Inserted: {wcInserted:N0}");
        Console.WriteLine($"Skipped: {wcSkipped:N0}");
        
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }
    
    if (input.ToLower() == "j")
    {
        Console.WriteLine("\n=== WorldCat Full Records Import (JSONL.ZST) ===");
        Console.WriteLine("This imports full WorldCat bibliographic records from JSONL.ZST file");
        Console.WriteLine("Table: allthethings.worldcat_records");
        Console.WriteLine("Contains: title, author, publisher, subjects, summaries, etc.");
        Console.WriteLine("\nDefault path: H:\\BookCity\\Downloads\\annas_archive_meta__aacid__worldcat__20250804T000000Z--20250804T000000Z.jsonl.seekable.zst");
        Console.WriteLine("\nEnter path to WorldCat JSONL.ZST file (or press Enter for default):");
        var jsonPath = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            jsonPath = @"H:\BookCity\Downloads\annas_archive_meta__aacid__worldcat__20250804T000000Z--20250804T000000Z.jsonl.seekable.zst";
        }
        
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"File not found: {jsonPath}");
            Console.WriteLine("\nPress any key to return to the menu...");
            Console.ReadKey();
            continue;
        }
        
        var jsonFileInfo = new FileInfo(jsonPath);
        Console.WriteLine($"\nFile: {jsonPath}");
        Console.WriteLine($"Size: {jsonFileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB (compressed)");
        
        // Create table if needed
        Console.WriteLine("\nCreating worldcat_records table if not exists...");
        var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", 
            "DataImports", "worldcat", "create_worldcat_records_table.sql");
        
        if (!File.Exists(schemaPath))
        {
            schemaPath = @"H:\Developer.BookCity\ABC.DiscoveryCity\DataImports\worldcat\create_worldcat_records_table.sql";
        }
        
        if (File.Exists(schemaPath))
        {
            try
            {
                var schemaSql = await File.ReadAllTextAsync(schemaPath);
                using var conn = new MySqlConnector.MySqlConnection(connectionString);
                await conn.OpenAsync();
                
                var statements = schemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var stmt in statements)
                {
                    var trimmed = stmt.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("--")) continue;
                    
                    try
                    {
                        using var cmd = new MySqlConnector.MySqlCommand(trimmed + ";", conn);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        if (!ex.Message.Contains("already exists") && !ex.Message.Contains("Duplicate"))
                        {
                            Console.WriteLine($"Schema warning: {ex.Message}");
                        }
                    }
                }
                Console.WriteLine("Table created/verified.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Schema error: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Schema file not found: {schemaPath}");
            Console.WriteLine("Continuing anyway (table may already exist)...");
        }
        
        // Check current row count
        long jsonCurrentRows = 0;
        try
        {
            using var checkConn = new MySqlConnector.MySqlConnection(connectionString);
            await checkConn.OpenAsync();
            using var checkCmd = new MySqlConnector.MySqlCommand(
                "SELECT COUNT(*) FROM allthethings.worldcat_records", checkConn);
            jsonCurrentRows = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
            Console.WriteLine($"Current rows in table: {jsonCurrentRows:N0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not check row count (table may not exist yet): {ex.Message}");
        }
        
        Console.WriteLine("\nSkip rows (for resuming, 0 = start from beginning):");
        if (jsonCurrentRows > 0)
        {
            Console.WriteLine($"(Suggestion: enter {jsonCurrentRows} to resume from where you left off)");
        }
        var jsonSkipInput = Console.ReadLine()?.Trim();
        long jsonSkipRows = 0;
        if (!string.IsNullOrWhiteSpace(jsonSkipInput))
        {
            long.TryParse(jsonSkipInput, out jsonSkipRows);
        }
        
        Console.WriteLine($"\nWill import from row {jsonSkipRows:N0}. Continue? (Y/n):");
        var jsonConfirm = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(jsonConfirm) && jsonConfirm.ToLower().StartsWith("n"))
        {
            continue;
        }
        
        // Run the loader
        var jsonLoader = new ABC.DiscoveryCity.MariaDB.WorldCatJsonLoader(connectionString, jsonPath, batchSize: 2000);
        var (jsonProcessed, jsonInserted, jsonSkipped) = await jsonLoader.LoadAsync(jsonSkipRows);
        
        Console.WriteLine($"\nImport complete!");
        Console.WriteLine($"Processed: {jsonProcessed:N0}");
        Console.WriteLine($"Inserted: {jsonInserted:N0}");
        Console.WriteLine($"Skipped: {jsonSkipped:N0}");
        
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }
    
    if (input.ToLower() == "c")
    {
        Console.WriteLine("\n=== aarecords_codes Import (Span-based) ===");
        Console.WriteLine("This imports the massive aarecords_codes table (~377GB compressed)");
        Console.WriteLine("Table: allthethings.aarecords_codes");
        Console.WriteLine("This is the main index linking codes to records - will take a LONG time!");
        Console.WriteLine("\nDefault path: H:\\BookCity\\Books\\aa_derived_mirror_metadata_20251121\\mariadb\\allthethings.aarecords_codes.00000.dat.gz");
        Console.WriteLine("\nEnter path to aarecords_codes .dat.gz file (or press Enter for default):");
        var codesPath = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrWhiteSpace(codesPath))
        {
            codesPath = @"H:\BookCity\Books\aa_derived_mirror_metadata_20251121\mariadb\allthethings.aarecords_codes.00000.dat.gz";
        }
        
        if (!File.Exists(codesPath))
        {
            Console.WriteLine($"File not found: {codesPath}");
            Console.WriteLine("\nPress any key to return to the menu...");
            Console.ReadKey();
            continue;
        }
        
        var codesFileInfo = new FileInfo(codesPath);
        Console.WriteLine($"\nFile: {codesPath}");
        Console.WriteLine($"Size: {codesFileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB (compressed)");
        Console.WriteLine("WARNING: This file is extremely large and may take days to import!");
        
        // Check current row count
        long codesCurrentRows = 0;
        try
        {
            using var checkConn = new MySqlConnector.MySqlConnection(connectionString);
            await checkConn.OpenAsync();
            using var checkCmd = new MySqlConnector.MySqlCommand(
                "SELECT table_rows FROM information_schema.tables WHERE table_schema='allthethings' AND table_name='aarecords_codes'", checkConn);
            var result = await checkCmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                codesCurrentRows = Convert.ToInt64(result);
            }
            Console.WriteLine($"Current rows in table (approx): {codesCurrentRows:N0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not check row count: {ex.Message}");
        }
        
        Console.WriteLine("\nSkip rows (for resuming, 0 = start from beginning):");
        if (codesCurrentRows > 0)
        {
            Console.WriteLine($"(Suggestion: enter {codesCurrentRows} to resume from where you left off)");
        }
        var codesSkipInput = Console.ReadLine()?.Trim();
        long codesSkipRows = 0;
        if (!string.IsNullOrWhiteSpace(codesSkipInput))
        {
            long.TryParse(codesSkipInput, out codesSkipRows);
        }
        
        Console.WriteLine($"\nWill import from row {codesSkipRows:N0}. Continue? (Y/n):");
        var codesConfirm = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(codesConfirm) && codesConfirm.ToLower().StartsWith("n"))
        {
            continue;
        }
        
        // Run the loader
        var codesLoader = new ABC.DiscoveryCity.MariaDB.AarecordsCodesLoader(connectionString, codesPath, batchSize: 10000);
        var (codesProcessed, codesInserted, codesSkipped) = await codesLoader.LoadAsync(codesSkipRows);
        
        Console.WriteLine($"\nImport complete!");
        Console.WriteLine($"Processed: {codesProcessed:N0}");
        Console.WriteLine($"Inserted: {codesInserted:N0}");
        Console.WriteLine($"Skipped: {codesSkipped:N0}");
        
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }
    string numberPart = isDataLinkRequest ? input.Substring(0, input.Length - 1) : input;

    if (int.TryParse(numberPart, out int choice) && choice > 0 && choice <= schemaFiles.Count)
    {
        var selectedSchemaFile = schemaFiles[choice - 1];
        
        if (isDataLinkRequest)
        {
            string schemaFileName = Path.GetFileName(selectedSchemaFile);
            string baseName = schemaFileName.Replace("-schema.sql.gz", "");
            var matchingDataFiles = allDataFiles.Where(d => Path.GetFileName(d).StartsWith(baseName + ".")).OrderBy(d => d).ToList();

            if (matchingDataFiles.Count == 0)
            {
                Console.WriteLine($"No data files found matching pattern: {baseName}.*.dat.gz");
                continue;
            }

            // Infer table name
            string tableName = "";
            var parts = baseName.Split('.');
            if (parts.Length >= 2)
            {
                tableName = parts[1]; 
            }

            Console.WriteLine($"Found {matchingDataFiles.Count} data file(s).");
            Console.WriteLine($"Inferred Table Name: {tableName}");
            Console.Write("Press Enter to confirm table name or type correct one: ");
            var manualTable = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(manualTable)) tableName = manualTable;

            foreach (var dataFile in matchingDataFiles)
            {
                string dataFileName = Path.GetFileName(dataFile);
                if (history.IsImported(dataFileName))
                {
                    Console.WriteLine($"Skipping already imported: {dataFileName}");
                    continue;
                }

                Console.WriteLine($"Importing {dataFileName}...");
                bool success = await SqlImporter.ImportDataGzAsync(dataFile, tableName);
                if (success)
                {
                    Console.Write("Import successful. Mark as DONE in history? (Y/n): ");
                    var mark = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(mark) || mark.ToLower().StartsWith("y"))
                    {
                        history.MarkAsImported(dataFileName);
                    }
                }
                else
                {
                    Console.WriteLine("Import failed. History NOT updated.");
                }
            }
        }
        else
        {
            string schemaFileName = Path.GetFileName(selectedSchemaFile);
            if (!history.IsImported(schemaFileName))
            {
                bool success = await SqlImporter.ImportSqlGzAsync(selectedSchemaFile);
                if (success) history.MarkAsImported(schemaFileName);
            }
            else
            {
                Console.WriteLine("Schema already imported. Re-importing anyway...");
                await SqlImporter.ImportSqlGzAsync(selectedSchemaFile);
                // No need to mark again, but good to allow re-run
            }
        }
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }

    if (input.ToLower() == "d")
    {
        Console.WriteLine("\nScanning for .dat.gz files...");
        var dataFiles = allDataFiles.OrderBy(f => f).ToList();

        if (dataFiles.Count == 0)
        {
            Console.WriteLine("No .dat.gz files found.");
            continue;
        }

        for (int i = 0; i < dataFiles.Count; i++)
        {
            var fileInfo = new FileInfo(dataFiles[i]);
            Console.WriteLine($"{i + 1}. {Path.GetFileName(dataFiles[i])} ({fileInfo.Length / 1024.0 / 1024.0:N2} MB)");
        }

        Console.WriteLine("\nEnter the number of the DATA file to import:");
        var dataInput = Console.ReadLine();
        if (int.TryParse(dataInput, out int dataChoice) && dataChoice > 0 && dataChoice <= dataFiles.Count)
        {
            var selectedDataFile = dataFiles[dataChoice - 1];
            string fileName = Path.GetFileName(selectedDataFile);
            
            // Infer table name from filename
            // Format: allthethings.TABLE_NAME.00000.dat.gz
            // Example: allthethings.isbndb_isbns.00000.dat.gz -> isbndb_isbns
            string tableName = "";
            var parts = fileName.Split('.');
            if (parts.Length >= 3)
            {
                tableName = parts[1]; // The second part is usually the table name
            }

            Console.WriteLine($"Inferred Table Name: {tableName}");
            Console.Write("Press Enter to confirm or type correct table name: ");
            var manualTable = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(manualTable)) tableName = manualTable;

            bool success = await SqlImporter.ImportDataGzAsync(selectedDataFile, tableName);
            if (success)
            {
                Console.Write("Import successful. Mark as DONE in history? (Y/n): ");
                var mark = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(mark) || mark.ToLower().StartsWith("y"))
                {
                    history.MarkAsImported(fileName);
                }
            }
        }
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
        continue;
    }

    if (input == "0")
    {
        var schemaFilesList = new List<string> {
            "allthethings-schema-create.sql.gz",
            "allthethings.aarecords_codes_bloomsbury_for_lookup-schema.sql.gz",
            "allthethings.aarecords_codes_cerlalc_for_lookup-schema.sql.gz",
            "allthethings.aarecords_codes_chinese_architecture_for_lookup-schema.sql.gz",
            "allthethings.aarecords_codes_czech_oo42hcks_for_lookup-schema.sql.gz",
            "allthethings.aarecords_codes_edsebk_for_lookup-schema.sql.gz",
            // Add other schemas here as needed
        };

        foreach (var schema in schemaFiles)
        {
            string fullPath = Path.Combine(baseFolder, schema);
            if (File.Exists(fullPath))
            {
                await SqlImporter.ImportSqlGzAsync(fullPath);
            }
            else
            {
                Console.WriteLine($"Skipping missing file: {schema}");
            }
        }
    }
    else if (int.TryParse(input, out int choice2) && choice2 > 0 && choice2 <= schemaFiles.Count)
    {
        var selectedFile = schemaFiles[choice2 - 1];
        bool success = await SqlImporter.ImportSqlGzAsync(selectedFile);
        if (success) history.MarkAsImported(Path.GetFileName(selectedFile));
        
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
    }
    else
    {
        Console.WriteLine("Invalid selection.");
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey();
    }
}

// ============================================================================
// Torrent Downloads Menu Handler
// ============================================================================

static async Task HandleTorrentDownloadsMenu(string connectionString)
{
    const string torrentsFolder = @"H:\BookCity\Torrents";
    const string downloadsFolder = @"H:\BookCity\Downloads";
    
    var manager = new TorrentDownloadManager(torrentsFolder, downloadsFolder);
    
    while (true)
    {
        Console.Clear();
        Console.WriteLine("=== Completed Torrent Downloads ===\n");
        Console.WriteLine($"Torrents folder: {torrentsFolder}");
        Console.WriteLine($"Downloads folder: {downloadsFolder}\n");
        
        var downloads = manager.GetCompletedDownloads();
        
        if (downloads.Count == 0)
        {
            Console.WriteLine("No completed torrent downloads found.");
            Console.WriteLine("\nPress any key to return to the main menu...");
            Console.ReadKey();
            return;
        }
        
        // Group by type for better display
        var byType = downloads.GroupBy(d => d.Type).OrderBy(g => g.Key.ToString());
        
        int index = 1;
        var indexMap = new Dictionary<int, TorrentDownloadManager.CompletedDownload>();
        
        foreach (var group in byType)
        {
            Console.WriteLine($"\n--- {group.Key} ({group.First().Description}) ---");
            
            foreach (var download in group)
            {
                var status = download.DownloadExists ? "✓" : "✗";
                var estMarker = download.IsEstimated ? "~" : "";
                var sizeStr = download.SizeBytes > 0 
                    ? $"{estMarker}{download.SizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB" 
                    : "N/A";
                var filesStr = download.FileCount > 1 
                    ? (download.FileCount >= 50000 ? $"{estMarker}50000+ files" : $"{estMarker}{download.FileCount:N0} files") 
                    : "";
                
                Console.WriteLine($"  {index}. [{status}] {download.TorrentName}");
                Console.WriteLine($"       Size: {sizeStr} {filesStr}");
                
                indexMap[index] = download;
                index++;
            }
        }
        
        Console.WriteLine($"\n[Q] Return to main menu");
        Console.WriteLine("\nEnter number to view details and import options:");
        
        var input = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "q")
        {
            return;
        }
        
        if (int.TryParse(input, out int choice) && indexMap.ContainsKey(choice))
        {
            var selected = indexMap[choice];
            await HandleSelectedTorrentDownload(selected, manager, connectionString);
        }
    }
}

static async Task HandleSelectedTorrentDownload(
    TorrentDownloadManager.CompletedDownload download, 
    TorrentDownloadManager manager,
    string connectionString)
{
    Console.Clear();
    Console.WriteLine($"=== {download.TorrentName} ===\n");
    Console.WriteLine($"Torrent file: {download.TorrentFile}");
    Console.WriteLine($"Download path: {download.DownloadPath}");
    Console.WriteLine($"Exists: {(download.DownloadExists ? "Yes" : "No")}");
    Console.WriteLine($"Type: {download.Type}");
    Console.WriteLine($"Description: {download.Description}");
    var estMarker = download.IsEstimated ? "~" : "";
    Console.WriteLine($"Size: {estMarker}{download.SizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB{(download.IsEstimated ? " (estimated)" : "")}");
    Console.WriteLine($"Files: {(download.FileCount >= 50000 ? $"{estMarker}50000+" : $"{estMarker}{download.FileCount:N0}")}{(download.IsEstimated ? " (estimated)" : "")}");
    
    var dataSource = manager.DetectDataSource(download.TorrentName);
    Console.WriteLine($"\nDetected data source: {dataSource ?? "Unknown"}");
    Console.WriteLine($"Suggested action: {manager.GetSuggestedAction(download)}");
    
    if (!download.DownloadExists)
    {
        Console.WriteLine("\n[!] Download not found at expected path. Torrent may still be downloading.");
        Console.WriteLine("\nPress any key to return...");
        Console.ReadKey();
        return;
    }
    
    // Show available actions based on type
    Console.WriteLine("\n--- Available Actions ---");
    
    switch (download.Type)
    {
        case TorrentDownloadManager.DownloadType.JsonlSeekableZst:
            Console.WriteLine("1. Import with WorldCatJsonLoader (for WorldCat data)");
            Console.WriteLine("2. Peek at file contents (first few records)");
            break;
            
        case TorrentDownloadManager.DownloadType.AacidDataFolder:
            Console.WriteLine("1. Catalog files (list and count)");
            Console.WriteLine("2. Peek at sample file contents");
            Console.WriteLine("3. [Future] Register in file catalog table");
            break;
            
        case TorrentDownloadManager.DownloadType.BookFilesFolder:
            Console.WriteLine("1. Catalog files (list by extension)");
            Console.WriteLine("2. [Future] Register in file catalog table");
            break;
            
        case TorrentDownloadManager.DownloadType.DatGzFile:
            Console.WriteLine("1. Peek at file contents (first few lines)");
            Console.WriteLine("2. Import with appropriate loader");
            break;
            
        default:
            Console.WriteLine("1. Peek at contents");
            break;
    }
    
    Console.WriteLine("Q. Return to torrent list");
    Console.WriteLine("\nSelect action:");
    
    var actionInput = Console.ReadLine()?.Trim();
    
    if (string.IsNullOrWhiteSpace(actionInput) || actionInput.ToLower() == "q")
    {
        return;
    }
    
    if (actionInput == "1")
    {
        await ExecuteAction1(download, connectionString);
    }
    else if (actionInput == "2")
    {
        await ExecuteAction2(download);
    }
    
    Console.WriteLine("\nPress any key to continue...");
    Console.ReadKey();
}

static async Task ExecuteAction1(TorrentDownloadManager.CompletedDownload download, string connectionString)
{
    switch (download.Type)
    {
        case TorrentDownloadManager.DownloadType.JsonlSeekableZst:
            if (download.TorrentName.Contains("worldcat"))
            {
                Console.WriteLine("\nStarting WorldCat JSON import...");
                Console.WriteLine("Enter skip rows (0 for start from beginning):");
                var skipInput = Console.ReadLine()?.Trim();
                long skipRows = 0;
                if (!string.IsNullOrWhiteSpace(skipInput))
                    long.TryParse(skipInput, out skipRows);
                
                var loader = new ABC.DiscoveryCity.MariaDB.WorldCatJsonLoader(
                    connectionString, 
                    download.DownloadPath,
                    batchSize: 5000);
                    
                await loader.LoadAsync(skipRows);
            }
            else
            {
                Console.WriteLine("No specific loader available for this JSONL file yet.");
            }
            break;
            
        case TorrentDownloadManager.DownloadType.AacidDataFolder:
        case TorrentDownloadManager.DownloadType.BookFilesFolder:
            // Catalog the files
            Console.WriteLine("\nCataloging files...\n");
            var files = Directory.GetFiles(download.DownloadPath, "*", SearchOption.AllDirectories);
            var byExt = files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                            .OrderByDescending(g => g.Count());
            
            Console.WriteLine($"Total files: {files.Length:N0}");
            Console.WriteLine($"Total size: {files.Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0 / 1024.0:F2} GB\n");
            
            Console.WriteLine("By extension:");
            foreach (var group in byExt.Take(10))
            {
                var ext = string.IsNullOrEmpty(group.Key) ? "(no extension)" : group.Key;
                var size = group.Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0;
                Console.WriteLine($"  {ext}: {group.Count():N0} files ({size:F1} MB)");
            }
            break;
            
        case TorrentDownloadManager.DownloadType.DatGzFile:
            Console.WriteLine("\nPeeking at file contents...\n");
            await PeekGzipFile(download.DownloadPath, 5);
            break;
            
        default:
            Console.WriteLine("Action not implemented for this type.");
            break;
    }
}

static async Task ExecuteAction2(TorrentDownloadManager.CompletedDownload download)
{
    switch (download.Type)
    {
        case TorrentDownloadManager.DownloadType.JsonlSeekableZst:
        case TorrentDownloadManager.DownloadType.DatGzFile:
            Console.WriteLine("\nPeeking at file contents...\n");
            await PeekGzipFile(download.DownloadPath, 5);
            break;
            
        case TorrentDownloadManager.DownloadType.AacidDataFolder:
        case TorrentDownloadManager.DownloadType.BookFilesFolder:
            // Sample a file
            var sampleFiles = Directory.GetFiles(download.DownloadPath).Take(3);
            foreach (var file in sampleFiles)
            {
                Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");
                var info = new FileInfo(file);
                Console.WriteLine($"Size: {info.Length / 1024.0:F1} KB");
                
                // Check magic bytes
                try
                {
                    using var fs = File.OpenRead(file);
                    var magic = new byte[4];
                    await fs.ReadAsync(magic, 0, 4);
                    var magicStr = System.Text.Encoding.ASCII.GetString(magic, 0, 2);
                    
                    var format = magicStr switch
                    {
                        "PK" => "ZIP archive",
                        "\x1f\x8b" => "GZIP",
                        "%P" => "PDF",
                        "Ra" => "RAR",
                        _ => $"Unknown ({BitConverter.ToString(magic)})"
                    };
                    Console.WriteLine($"Format: {format}");
                }
                catch { }
            }
            break;
            
        default:
            Console.WriteLine("Peek not implemented for this type.");
            break;
    }
}

static async Task PeekGzipFile(string filePath, int lineCount)
{
    try
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        
        // Check if it's gzipped
        var magic = new byte[2];
        await fs.ReadAsync(magic, 0, 2);
        fs.Position = 0;
        
        Stream readStream;
        if (magic[0] == 0x1f && magic[1] == 0x8b)
        {
            readStream = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
        }
        else
        {
            readStream = fs;
        }
        
        using var reader = new StreamReader(readStream);
        for (int i = 0; i < lineCount && !reader.EndOfStream; i++)
        {
            var line = await reader.ReadLineAsync();
            if (line != null)
            {
                // Truncate long lines
                if (line.Length > 200)
                    line = line.Substring(0, 200) + "...";
                Console.WriteLine($"[{i + 1}] {line}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading file: {ex.Message}");
    }
}

// ============================================================================
// File Catalog Menu Handler
// ============================================================================

static async Task HandleFileCatalogMenu(string connectionString)
{
    const string downloadsFolder = @"H:\BookCity\Downloads";
    
    while (true)
    {
        Console.Clear();
        Console.WriteLine("=== BookCity File Catalog ===\n");
        Console.WriteLine($"Default scan folder: {downloadsFolder}\n");
        
        Console.WriteLine("Options:");
        Console.WriteLine("1. Scan all downloads (full scan - requires collection name)");
        Console.WriteLine("2. Scan specific folder");
        Console.WriteLine("3. View collection statistics");
        Console.WriteLine("4. List available collections");
        Console.WriteLine("5. Link HathiTrust files (extract HTID from ZIPs)");
        Console.WriteLine("6. View HathiTrust linking summary");
        Console.WriteLine("Q. Return to main menu");
        Console.WriteLine();
        
        Console.Write("Enter choice: ");
        var input = Console.ReadLine()?.Trim().ToLower();
        
        if (string.IsNullOrEmpty(input) || input == "q")
            return;
        
        var loader = new FileCatalogLoader(connectionString);
        
        switch (input)
        {
            case "1":
                Console.Write("\nEnter collection code (e.g., libgen, hathitrust, unknown): ");
                var collection = Console.ReadLine()?.Trim().ToLower() ?? "unknown";
                Console.WriteLine($"\nStarting scan of {downloadsFolder} for collection '{collection}'...");
                await loader.ScanFolderAsync(downloadsFolder, collection);
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                break;
                
            case "2":
                Console.Write("\nEnter folder path to scan: ");
                var folderPath = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(folderPath))
                {
                    Console.Write("Enter collection code: ");
                    var coll = Console.ReadLine()?.Trim().ToLower() ?? "unknown";
                    await loader.ScanFolderAsync(folderPath, coll);
                }
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                break;
                
            case "3":
                await loader.PrintStatsAsync();
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                break;
                
            case "4":
                await ShowCollectionStats(connectionString);
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                break;
                
            case "5":
                Console.WriteLine("\nLinking HathiTrust files to catalog entries...");
                Console.WriteLine("This will extract HTID from each ZIP file and update the database.\n");
                var linker = new HathiTrustLinker(connectionString);
                await linker.LinkFilesAsync();
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                break;
                
            case "6":
                await ShowHathiTrustSummary(connectionString);
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                break;
        }
    }
}

static async Task ShowCollectionStats(string connectionString)
{
    using var conn = new MySqlConnector.MySqlConnection(connectionString);
    await conn.OpenAsync();
    
    var sql = @"
        SELECT CollectionCode, CollectionName, FileCount, 
               ROUND(TotalSizeBytes / 1024 / 1024 / 1024, 2) as SizeGB
        FROM bookcitycollection 
        ORDER BY FileCount DESC";
    
    using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
    using var reader = await cmd.ExecuteReaderAsync();
    
    Console.WriteLine("\n=== Collection Statistics ===");
    Console.WriteLine("─────────────────────────────────────────────────────────────");
    Console.WriteLine($"{"Collection",-20} {"Name",-30} {"Files",10} {"Size (GB)",12}");
    Console.WriteLine("─────────────────────────────────────────────────────────────");
    
    while (await reader.ReadAsync())
    {
        var code = reader.GetString(0);
        var name = reader.GetString(1);
        var fileCount = reader.GetInt64(2);
        var sizeGb = reader.GetDecimal(3);
        
        Console.WriteLine($"{code,-20} {name,-30} {fileCount,10:N0} {sizeGb,12:N2}");
    }
}

static async Task ShowHathiTrustSummary(string connectionString)
{
    using var conn = new MySqlConnector.MySqlConnection(connectionString);
    await conn.OpenAsync();
    
    // First check if the view exists, if not provide basic stats
    var sql = @"
        SELECT 
            COUNT(*) AS TotalFiles,
            SUM(CASE WHEN SourceIdType = 'htid' THEN 1 ELSE 0 END) AS LinkedFiles,
            SUM(CASE WHEN SourceIdType = 'aacid' OR SourceIdType IS NULL THEN 1 ELSE 0 END) AS UnlinkedFiles,
            ROUND(SUM(FileSize) / 1024 / 1024 / 1024, 2) AS TotalSizeGB
        FROM bookcityfile f
        INNER JOIN bookcitycollection c ON f.CollectionId = c.CollectionId
        WHERE c.CollectionCode = 'hathitrust'";
    
    using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
    using var reader = await cmd.ExecuteReaderAsync();
    
    Console.WriteLine("\n=== HathiTrust Linking Summary ===");
    Console.WriteLine("─────────────────────────────────────────────────────────────");
    
    if (await reader.ReadAsync())
    {
        var total = reader.GetInt64(0);
        var linked = reader.GetInt64(1);
        var unlinked = reader.GetInt64(2);
        var sizeGb = reader.GetDecimal(3);
        var pct = total > 0 ? (linked * 100.0 / total) : 0;
        
        Console.WriteLine($"Total HathiTrust Files:  {total:N0}");
        Console.WriteLine($"Linked (HTID extracted): {linked:N0} ({pct:N1}%)");
        Console.WriteLine($"Unlinked (needs work):   {unlinked:N0}");
        Console.WriteLine($"Total Size:              {sizeGb:N2} GB");
    }
    
    await reader.CloseAsync();
    
    // Show sample of linked books with metadata
    var sampleSql = @"
        SELECT f.SourceId, h.title, h.author, f.ResourcePath
        FROM bookcityfile f
        INNER JOIN bookcitycollection c ON f.CollectionId = c.CollectionId
        LEFT JOIN hathitrust_records h ON f.SourceId = h.htid
        WHERE c.CollectionCode = 'hathitrust' AND f.SourceIdType = 'htid'
        LIMIT 5";
    
    using var cmd2 = new MySqlConnector.MySqlCommand(sampleSql, conn);
    using var reader2 = await cmd2.ExecuteReaderAsync();
    
    Console.WriteLine("\n--- Sample Linked Books ---");
    while (await reader2.ReadAsync())
    {
        var htid = reader2.IsDBNull(0) ? "?" : reader2.GetString(0);
        var title = reader2.IsDBNull(1) ? "(no metadata)" : reader2.GetString(1);
        var author = reader2.IsDBNull(2) ? "" : reader2.GetString(2);
        var url = reader2.IsDBNull(3) ? "" : reader2.GetString(3);
        
        Console.WriteLine($"\n  HTID:   {htid}");
        if (!string.IsNullOrEmpty(title)) Console.WriteLine($"  Title:  {(title.Length > 60 ? title[..60] + "..." : title)}");
        if (!string.IsNullOrEmpty(author)) Console.WriteLine($"  Author: {author}");
        if (!string.IsNullOrEmpty(url)) Console.WriteLine($"  URL:    {url}");
    }
}