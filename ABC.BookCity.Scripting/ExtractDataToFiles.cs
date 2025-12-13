// Extract large HathiTrust catalog file into smaller chunks for import
// Copy this script to a folder containing the data file and run: dotnet run
// Will find the largest file and split it into ~10MB chunks in ./chunks subfolder

string sourceFolder = Directory.GetCurrentDirectory();
string outputFolder = Path.Combine(sourceFolder, "chunks");
long targetFileSizeBytes = 10 * 1024 * 1024; // 10 MB per file
string outputExtension = ".txt"; // Tab-separated values

Console.WriteLine($"Working directory: {sourceFolder}");
await ExtractToChunksAsync(sourceFolder, outputFolder, targetFileSizeBytes, outputExtension, Console.WriteLine);

async Task ExtractToChunksAsync(string sourceFolder, string outputFolder, long targetSize, string extension, Action<string> log)
{
    // Find the largest file in the source folder (the main data file)
    var sourceFile = Directory.GetFiles(sourceFolder)
        .Select(f => new FileInfo(f))
        .OrderByDescending(f => f.Length)
        .FirstOrDefault();

    if (sourceFile == null)
    {
        log($"No files found in {sourceFolder}");
        return;
    }

    log($"Source file: {sourceFile.Name}");
    log($"Size: {sourceFile.Length / 1024.0 / 1024.0:F2} MB");
    log($"Target chunk size: {targetSize / 1024.0 / 1024.0:F2} MB");

    // Create output folder
    Directory.CreateDirectory(outputFolder);

    // Check for existing chunks to resume (only count actual .txt files, not .processing.txt or .done.txt)
    var existingChunks = Directory.GetFiles(outputFolder, $"*{extension}")
        .Where(f => !f.EndsWith(".processing.txt") && !f.EndsWith(".done.txt"))
        .Select(f => new FileInfo(f))
        .OrderBy(f => f.Name)
        .ToList();

    long bytesToSkip = 0;
    int startChunk = 1;

    if (existingChunks.Any())
    {
        // Calculate bytes already processed (sum of complete chunks)
        // The last chunk might be incomplete, so we'll rewrite it
        var completeChunks = existingChunks.Take(existingChunks.Count - 1).ToList();
        bytesToSkip = completeChunks.Sum(f => f.Length);
        startChunk = completeChunks.Count + 1;
        
        // Delete the last (potentially incomplete) chunk
        if (existingChunks.Any())
        {
            var lastChunk = existingChunks.Last();
            log($"Resuming from chunk {startChunk}, deleting potentially incomplete: {lastChunk.Name}");
            File.Delete(lastChunk.FullName);
        }

        if (bytesToSkip > 0)
        {
            log($"Skipping {bytesToSkip / 1024.0 / 1024.0:F2} MB already processed ({completeChunks.Count} complete chunks)");
        }
    }

    int chunkNumber = startChunk;
    long totalBytesRead = bytesToSkip;
    long currentChunkBytes = 0;
    StreamWriter? writer = null;
    var startTime = DateTime.Now;
    var lastProgressTime = DateTime.Now;

    try
    {
        using var reader = new StreamReader(sourceFile.FullName);
        
        // Skip already processed lines
        if (bytesToSkip > 0)
        {
            log("Skipping to resume position...");
            long skipped = 0;
            string? skipLine;
            while (skipped < bytesToSkip && (skipLine = await reader.ReadLineAsync()) != null)
            {
                skipped += System.Text.Encoding.UTF8.GetByteCount(skipLine) + 1; // +1 for newline
            }
            log($"Skipped to position, ready to continue...");
        }

        string? currentLine;
        while ((currentLine = await reader.ReadLineAsync()) != null)
        {
            // Start new chunk if needed
            if (writer == null)
            {
                var chunkPath = Path.Combine(outputFolder, $"hathi_chunk_{chunkNumber:D4}{extension}");
                writer = new StreamWriter(chunkPath, false, System.Text.Encoding.UTF8);
                log($"Writing chunk {chunkNumber}: {Path.GetFileName(chunkPath)}");
                currentChunkBytes = 0;
            }

            await writer.WriteLineAsync(currentLine);
            var lineBytes = System.Text.Encoding.UTF8.GetByteCount(currentLine) + 1;
            currentChunkBytes += lineBytes;
            totalBytesRead += lineBytes;

            // Progress update every 5 seconds
            if ((DateTime.Now - lastProgressTime).TotalSeconds >= 5)
            {
                var elapsed = DateTime.Now - startTime;
                var mbRead = totalBytesRead / 1024.0 / 1024.0;
                var mbTotal = sourceFile.Length / 1024.0 / 1024.0;
                var percent = (double)totalBytesRead / sourceFile.Length * 100;
                var mbPerSec = mbRead / elapsed.TotalSeconds;
                var remainingMb = mbTotal - mbRead;
                var etaSeconds = mbPerSec > 0 ? remainingMb / mbPerSec : 0;
                var eta = TimeSpan.FromSeconds(etaSeconds);
                
                log($"Progress: {mbRead:F0}/{mbTotal:F0} MB ({percent:F1}%) - {mbPerSec:F1} MB/s - ETA: {eta:hh\\:mm\\:ss}");
                lastProgressTime = DateTime.Now;
            }

            // Check if chunk is full
            if (currentChunkBytes >= targetSize)
            {
                await writer.FlushAsync();
                writer.Dispose();
                writer = null;
                chunkNumber++;
            }
        }

        // Close final chunk
        if (writer != null)
        {
            await writer.FlushAsync();
            writer.Dispose();
        }

        var totalElapsed = DateTime.Now - startTime;
        log($"");
        log($"=== Extraction Complete ===");
        log($"Total chunks created: {chunkNumber}");
        log($"Total size: {totalBytesRead / 1024.0 / 1024.0:F2} MB");
        log($"Time elapsed: {totalElapsed:hh\\:mm\\:ss}");
        log($"Output folder: {outputFolder}");
    }
    finally
    {
        writer?.Dispose();
    }
}
