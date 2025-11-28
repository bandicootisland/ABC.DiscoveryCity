using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace ABC.BookCity.Importer;

public class DataBatchingImporter
{
    private readonly string _connectionString;
    private readonly string _7zPath;
    private readonly string _historyFile;

    public DataBatchingImporter(string connectionString, string sevenZipPath = @"C:\Program Files\7-Zip\7z.exe")
    {
        _connectionString = connectionString;
        _7zPath = sevenZipPath;
        _historyFile = "import_progress.json";
    }

    public async Task ImportArchiveAsync(string archivePath)
    {
        Console.WriteLine($"Starting import from archive: {archivePath}");

        if (!File.Exists(archivePath))
        {
            Console.WriteLine("Archive file not found.");
            return;
        }

        // 1. Start 7-Zip process to stream to stdout
        var psi = new ProcessStartInfo
        {
            FileName = _7zPath,
            // e: extract
            // -so: write to stdout
            // -y: assume yes on all queries
            Arguments = $"e -so \"{archivePath}\" -y",
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Capture errors
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        
        // Handle 7z errors
        process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine($"7z Error: {e.Data}"); };

        try
        {
            process.Start();
            process.BeginErrorReadLine();

            // 2. Process the stream
            using var stream = process.StandardOutput.BaseStream;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await ProcessSqlStreamAsync(reader, Path.GetFileName(archivePath));

            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running 7z: {ex.Message}");
            Console.WriteLine("Ensure 7-Zip is installed and the path is correct.");
        }
    }

    private async Task ProcessSqlStreamAsync(StreamReader reader, string fileName)
    {
        long linesRead = 0;
        long linesSkipped = 0;
        long lastCheckpoint = LoadCheckpoint(fileName);
        
        Console.WriteLine($"Resuming from line {lastCheckpoint:N0}...");

        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var batchBuffer = new StringBuilder();
        int batchCount = 0;
        const int BatchSize = 1000; // Rows per batch

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            linesRead++;

            // Resume Logic: Skip until we reach the checkpoint
            if (linesRead <= lastCheckpoint)
            {
                linesSkipped++;
                if (linesSkipped % 100000 == 0) Console.Write($"\rSkipping... {linesSkipped:N0} lines");
                continue;
            }

            if (linesSkipped > 0 && linesRead == lastCheckpoint + 1) Console.WriteLine("\nResumed processing.");

            // Basic Parsing
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("--") || line.StartsWith("/*"))
                continue;

            // Handle Multi-line Statements
            // If a line doesn't end with ';', we must keep reading until we find it.
            var statementBuilder = new StringBuilder(line);
            while (!line.TrimEnd().EndsWith(";") && (line = await reader.ReadLineAsync()) != null)
            {
                linesRead++;
                statementBuilder.AppendLine(line);
            }
            
            string fullStatement = statementBuilder.ToString();

            if (fullStatement.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase))
            {
                // Heuristic: If statement length > 100KB, it's probably already a bulk insert.
                // We execute it immediately to avoid memory pressure.
                if (fullStatement.Length > 100000)
                {
                    await ExecuteSqlAsync(connection, fullStatement);
                    SaveCheckpoint(fileName, linesRead);
                }
                else
                {
                    // It's a small insert, add to batch
                    batchBuffer.AppendLine(fullStatement);
                    batchCount++;

                    if (batchCount >= BatchSize)
                    {
                        await ExecuteSqlAsync(connection, batchBuffer.ToString());
                        batchBuffer.Clear();
                        batchCount = 0;
                        SaveCheckpoint(fileName, linesRead);
                        Console.Write($"\rProcessed {linesRead:N0} lines...");
                    }
                }
            }
            else
            {
                // DDL or other commands (CREATE, DROP, USE, LOCK)
                // Execute immediately
                await ExecuteSqlAsync(connection, fullStatement);
                SaveCheckpoint(fileName, linesRead);
            }
        }

        // Flush remaining batch
        if (batchBuffer.Length > 0)
        {
            await ExecuteSqlAsync(connection, batchBuffer.ToString());
        }

        Console.WriteLine($"\nImport of {fileName} completed.");
        SaveCheckpoint(fileName, linesRead); // Or mark as done
    }

    private async Task ExecuteSqlAsync(MySqlConnection connection, string sql)
    {
        try
        {
            using var cmd = new MySqlCommand(sql, connection);
            cmd.CommandTimeout = 300; // 5 minutes
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nSQL Error: {ex.Message}");
            // Decide whether to stop or continue. 
            // For now, we log and continue, but in production maybe stop?
        }
    }

    private void SaveCheckpoint(string fileName, long lineNum)
    {
        // Simple JSON: {"filename": lineNum}
        // In real app, use a proper object/dictionary
        File.WriteAllText(_historyFile, $"{lineNum}");
    }

    private long LoadCheckpoint(string fileName)
    {
        if (File.Exists(_historyFile))
        {
            if (long.TryParse(File.ReadAllText(_historyFile), out long line))
                return line;
        }
        return 0;
    }
}
