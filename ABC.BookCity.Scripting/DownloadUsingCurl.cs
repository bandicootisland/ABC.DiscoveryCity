
using System.Text.RegularExpressions;

string downloadDir=@"H:\BookCity\Books";

var scriptlocation=@"H:\Developer.BookCity\ABC.BookCity\DataImports\data-imports\scripts\download_libgenli_proxies.sh";

var scriptContent=System.IO.File.ReadAllText(scriptlocation);
await DownloadCurlProxiesAsync(scriptContent,downloadDir, (s)=>{
    Console.WriteLine(s);
});
async Task DownloadCurlProxiesAsync(string scriptContent,string downloadDir, Action<string> onLog)
{
    // Parse lines like: curl ... --socks5-hostname proxy -O url &
    // We look for -O followed by the URL
    var matches = Regex.Matches(scriptContent, @"curl\s+.*?-O\s+(\S+)");
    
    if (matches.Count == 0)
    {
        onLog("No curl download URLs found.");
        return;
    }

    onLog($"Found {matches.Count} files to download.");
    onLog("Note: Downloading sequentially and ignoring proxies (using local connection).");
    
    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromHours(24);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

    foreach (Match match in matches)
    {
        var url = match.Groups[1].Value;
        // Remove trailing & if captured (though \S+ shouldn't capture it if there is a space)
        url = url.TrimEnd('&'); 
        
        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        var destPath = Path.Combine(downloadDir, fileName);

        if (File.Exists(destPath))
        {
            onLog($"Skipping {fileName} (already exists).");
            continue;
        }

        onLog($"Downloading {fileName}...");
        try
        {
            // Note: We are ignoring the proxy for now and trying direct download.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                onLog($"Error downloading {fileName}: {response.StatusCode}");
                continue;
            }
            
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var s = await response.Content.ReadAsStreamAsync();
            using var fs = new FileStream(destPath, FileMode.Create);
            
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;
            var lastLog = DateTime.Now;

            while ((bytesRead = await s.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fs.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if ((DateTime.Now - lastLog).TotalSeconds > 2)
                {
                    var percent = totalBytes != -1 ? (double)totalRead / totalBytes * 100 : -1;
                    var mbRead = totalRead / 1024.0 / 1024.0;
                    onLog($"Downloaded {mbRead:F2} MB {(percent != -1 ? $"({percent:F2}%)" : "")}");
                    lastLog = DateTime.Now;
                }
            }
            
            onLog($"Downloaded {fileName}.");
        }
        catch (Exception ex)
        {
            onLog($"Failed to download {fileName}: {ex.Message}");
        }
        
        // Add a small delay to be nice
        await Task.Delay(1000);
    }
}