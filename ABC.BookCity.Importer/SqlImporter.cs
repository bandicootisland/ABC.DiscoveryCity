using System.IO.Compression;
using MySqlConnector;

namespace ABC.BookCity.Importer;

public static class SqlImporter
{
    // Switched to 'root' because the script tries to access/create the 'allthethings' database,
    // which our limited 'bookcity_user' doesn't have permissions for.
    // Added AllowLoadLocalInfile=true for bulk loading
    private static string ConnectionString = "Server=localhost;Port=3306;User=root;Password=password;AllowLoadLocalInfile=true;";

    public static async Task<bool> ImportSqlGzAsync(string gzFilePath)
    {
        Console.WriteLine($"Starting import for: {Path.GetFileName(gzFilePath)}");

        if (!File.Exists(gzFilePath))
        {
            Console.WriteLine("File not found!");
            return false;
        }

        try
        {
            using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            Console.WriteLine("Connected to MariaDB.");

            // Try to select the database if it exists
            try 
            {
                using var useCmd = new MySqlCommand("USE allthethings;", connection);
                await useCmd.ExecuteNonQueryAsync();
            }
            catch 
            {
                // Ignore error if DB doesn't exist yet (e.g. during the very first create script)
            }

            using var fileStream = File.OpenRead(gzFilePath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);

            string? line;
            var sqlBuffer = new System.Text.StringBuilder();
            int statementCount = 0;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("--") || line.StartsWith("/*"))
                    continue;

                sqlBuffer.AppendLine(line);

                if (line.TrimEnd().EndsWith(";"))
                {
                    // Execute the statement
                    var sql = sqlBuffer.ToString();
                    using var command = new MySqlCommand(sql, connection);
                    command.CommandTimeout = 300; // 5 minutes for large inserts
                    await command.ExecuteNonQueryAsync();

                    sqlBuffer.Clear();
                    statementCount++;
                    if (statementCount % 100 == 0) Console.Write(".");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Import complete! Executed {statementCount} statements.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during import: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> ImportDataGzAsync(string gzFilePath, string tableName)
    {
        Console.WriteLine($"Starting DATA import for: {Path.GetFileName(gzFilePath)} into table '{tableName}'");

        if (!File.Exists(gzFilePath))
        {
            Console.WriteLine("File not found!");
            return false;
        }

        try
        {
            using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            Console.WriteLine("Connected to MariaDB.");

            // Ensure we are using the correct database
            using (var useCmd = new MySqlCommand("USE allthethings;", connection))
            {
                await useCmd.ExecuteNonQueryAsync();
            }

            // We will use MySqlBulkLoader for high performance
            // However, MySqlBulkLoader usually requires a local file path that the SERVER can access if Local=false.
            // Since we are running outside Docker but DB is inside, we must use Local=true (LOAD DATA LOCAL INFILE).
            // But standard BulkLoader doesn't support GZIP streams directly.
            // So we have to decompress to a temp file first, OR use a stream wrapper if the library supports it.
            // MySqlConnector supports SourceStream for BulkLoader.

            using var fileStream = File.OpenRead(gzFilePath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);

            var bulkLoader = new MySqlBulkLoader(connection)
            {
                TableName = tableName,
                SourceStream = gzipStream, // Stream directly from GZIP
                FieldTerminator = ",",
                FieldQuotationCharacter = '"',
                EscapeCharacter = '\\',
                LineTerminator = "\n",
                NumberOfLinesToSkip = 1, // Skip header row
                Local = true, // Essential for client-side stream
                ConflictOption = MySqlBulkLoaderConflictOption.Ignore // Ignore duplicates
            };

            // Handle NULL values represented as \N in the file
            // Note: MySqlBulkLoader doesn't have a direct "NullValue" property like some tools.
            // But standard MySQL LOAD DATA handles \N as NULL by default if not escaped.
            // Our file has \N.

            Console.WriteLine("Bulk loading data... this may take a while.");
            int rowsAffected = await bulkLoader.LoadAsync();

            Console.WriteLine($"Import complete! Inserted {rowsAffected} rows.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during data import: {ex.Message}");
            if (ex.Message.Contains("Loading local data is disabled"))
            {
                Console.WriteLine("HINT: You may need to add 'AllowLoadLocalInfile=true' to your connection string.");
            }
            return false;
        }
    }

    public static async Task VerifyGzipAsync(string gzFilePath)
    {
        Console.WriteLine($"Verifying GZip integrity for: {Path.GetFileName(gzFilePath)}");

        if (!File.Exists(gzFilePath))
        {
            Console.WriteLine("File not found!");
            return;
        }

        long totalBytes = 0;
        try
        {
            using (var fs = File.OpenRead(gzFilePath))
            {
                var header = new byte[10];
                int readCount = await fs.ReadAsync(header, 0, 10);
                Console.WriteLine($"First {readCount} bytes: {BitConverter.ToString(header).Replace("-", " ")}");
                
                if (readCount >= 2)
                {
                    if (header[0] == 0x1F && header[1] == 0x8B)
                    {
                        Console.WriteLine("Magic Number: OK (GZip)");
                        if (readCount >= 3)
                        {
                            Console.WriteLine($"Compression Method: {header[2]} (8 = Deflate)");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Magic Number: INVALID (Not a GZip file?)");
                    }
                }
            }

            Console.WriteLine("Reading stream... (this reads the whole file to check for errors)");
            using var fileStream = File.OpenRead(gzFilePath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var nullStream = Stream.Null;
            
            var buffer = new byte[81920]; // 80KB buffer
            int read;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await gzipStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalBytes += read;
                if (totalBytes % (1024 * 1024 * 100) == 0) // Every 100MB
                {
                    Console.Write($"\rRead {totalBytes / 1024 / 1024:N0} MB...");
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"\nVerification PASSED! File is valid.");
            Console.WriteLine($"Decompressed Size: {totalBytes / 1024 / 1024:N2} MB");
            Console.WriteLine($"Time taken: {stopwatch.Elapsed}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nVerification FAILED at processed bytes ~{totalBytes / 1024 / 1024:N2} MB.");
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine("The file is likely incomplete (sparse) or corrupted.");
        }
    }
}
