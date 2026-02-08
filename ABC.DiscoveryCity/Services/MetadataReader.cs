using System.IO.Compression;
using System.Text.Json;

namespace ABC.DiscoveryCity.Services;

public static class MetadataReader
{
    public static async Task<List<string>> ReadFirstLinesAsync(Stream fileStream, int lineCount = 20)
    {
        var lines = new List<string>();
        try
        {
            // We assume the file is GZipped based on the extension from the user's list
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);

            int count = 0;
            while (count < lineCount)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                lines.Add(line);
                count++;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Error reading file: {ex.Message}");
        }

        return lines;
    }
}
