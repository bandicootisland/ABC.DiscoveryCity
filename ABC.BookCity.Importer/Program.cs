using ABC.BookCity.Importer;

Console.WriteLine("ABC.BookCity Importer Service");
Console.WriteLine("-----------------------------");

string baseFolder = @"H:\BookCity\Books\aa_derived_mirror_metadata_20251121\mariadb";
string historyFile = @"H:\_DOCKER-DATA\import_history.json";
var history = new ImportHistory(historyFile);

if (!Directory.Exists(baseFolder))
{
    Console.WriteLine($"Directory not found: {baseFolder}");
    Console.WriteLine("Please ensure the torrent has started downloading.");
    return;
}

while (true)
{
    Console.WriteLine("\nAvailable files to import:");
    Console.WriteLine("0. [BATCH] Import Standard Schemas (Create DB + Tables)");
    Console.WriteLine("D. [DATA] List all Data Files (.dat.gz)");
    Console.WriteLine("V. [UTIL] Verify GZip Integrity of a file");
    
    var schemaFiles = Directory.GetFiles(baseFolder, "*.sql.gz")
                         .OrderBy(f => f)
                         .ToList();
    var allDataFiles = Directory.GetFiles(baseFolder, "*.dat.gz");

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
