using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

var folderPath = @"H:\BookCity\Downloads\87500000";
var zipFiles = Directory.GetFiles(folderPath, "*.zip").OrderBy(f => f).Take(3);

if (!zipFiles.Any())
{
    Console.WriteLine($"No zip files found in {folderPath}");
    return;
}

foreach (var zipPath in zipFiles)
{
    Console.WriteLine($"\n--- Inspecting Zip: {Path.GetFileName(zipPath)} ---");
    try
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        Console.WriteLine($"Total entries: {archive.Entries.Count}");
        
        // List first 10 entries to see naming convention
        var sampleEntries = archive.Entries.Take(10);
        foreach (var entry in sampleEntries)
        {
            Console.WriteLine($"  File: {entry.FullName} ({entry.Length} bytes)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error reading zip: {ex.Message}");
    }
}
