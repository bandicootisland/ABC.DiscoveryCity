using MySqlConnector;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// Manages downloading and importing HathiTrust catalog files (hathifiles).
/// Source: https://www.hathitrust.org/hathifiles
/// </summary>
public class HathiCatalogImporter
{
    private readonly string _connectionString;
    private readonly string _downloadPath;
    private readonly HttpClient _httpClient;
    
    private const string FileListUrl = "https://www.hathitrust.org/files/hathifiles/hathi_file_list.json";
    private const string BaseDownloadUrl = "https://www.hathitrust.org/files/hathifiles/";
    
    public HathiCatalogImporter(string connectionString, string downloadPath)
    {
        _connectionString = connectionString;
        _downloadPath = downloadPath;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromHours(2); // Large files need long timeout
        
        Directory.CreateDirectory(_downloadPath);
    }

    /// <summary>
    /// Syncs the file list from HathiTrust and updates the hathi_file_list table.
    /// </summary>
    public async Task<int> SyncFileListAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[HathiCatalog] Fetching file list from {FileListUrl}");
        
        var json = await _httpClient.GetStringAsync(FileListUrl, ct);
        var fileList = JsonSerializer.Deserialize<List<HathiFileInfo>>(json, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        
        Console.WriteLine($"[HathiCatalog] Found {fileList.Count} files in list");
        
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        int added = 0;
        foreach (var file in fileList)
        {
            var fileType = DetermineFileType(file.Filename);
            var fileDate = ParseFileDate(file.Filename);
            
            const string sql = @"
                INSERT INTO hathi_file_list (file_name, file_url, file_type, file_date, file_size_bytes)
                VALUES (@fileName, @fileUrl, @fileType, @fileDate, @fileSize)
                ON DUPLICATE KEY UPDATE 
                    file_size_bytes = @fileSize,
                    updated_at = CURRENT_TIMESTAMP";
            
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@fileName", file.Filename);
            cmd.Parameters.AddWithValue("@fileUrl", BaseDownloadUrl + file.Filename);
            cmd.Parameters.AddWithValue("@fileType", fileType);
            cmd.Parameters.AddWithValue("@fileDate", fileDate);
            cmd.Parameters.AddWithValue("@fileSize", file.Size);
            
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows > 0) added++;
        }
        
        Console.WriteLine($"[HathiCatalog] Added/updated {added} files in database");
        return added;
    }

    /// <summary>
    /// Downloads a file from the file list.
    /// </summary>
    public async Task<string?> DownloadFileAsync(string fileName, 
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        // Get file info
        const string selectSql = "SELECT file_url, file_size_bytes FROM hathi_file_list WHERE file_name = @fileName";
        await using var selectCmd = new MySqlCommand(selectSql, connection);
        selectCmd.Parameters.AddWithValue("@fileName", fileName);
        
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            Console.WriteLine($"[HathiCatalog] File not found in list: {fileName}");
            return null;
        }
        
        var fileUrl = reader.GetString(0);
        var expectedSize = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        await reader.CloseAsync();
        
        var localPath = Path.Combine(_downloadPath, fileName);
        
        // Update status to downloading
        await UpdateDownloadStatusAsync(connection, fileName, "downloading", null, null, ct);
        
        Console.WriteLine($"[HathiCatalog] Downloading {fileName} ({expectedSize / 1024 / 1024:N0} MB)...");
        
        try
        {
            using var response = await _httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
            
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            var lastReport = DateTime.UtcNow;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                totalRead += bytesRead;
                
                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 5)
                {
                    progress?.Report((totalRead, totalBytes));
                    Console.WriteLine($"[HathiCatalog] Downloaded {totalRead / 1024 / 1024:N0} / {totalBytes / 1024 / 1024:N0} MB ({100.0 * totalRead / totalBytes:N1}%)");
                    lastReport = DateTime.UtcNow;
                }
            }
            
            // Update status to completed
            await UpdateDownloadStatusAsync(connection, fileName, "completed", localPath, null, ct);
            Console.WriteLine($"[HathiCatalog] Download complete: {localPath}");
            
            return localPath;
        }
        catch (Exception ex)
        {
            await UpdateDownloadStatusAsync(connection, fileName, "failed", null, ex.Message, ct);
            Console.WriteLine($"[HathiCatalog] Download failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Imports a downloaded hathifile into the hathi_catalog table.
    /// </summary>
    public async Task<(int imported, int updated, int skipped)> ImportFileAsync(
        string fileName,
        bool isFullLoad = false,
        bool resumeImport = false,
        IProgress<(int processed, int imported)>? progress = null,
        CancellationToken ct = default)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        // Get local path
        const string selectSql = "SELECT local_path FROM hathi_file_list WHERE file_name = @fileName AND download_status = 'completed'";
        await using var selectCmd = new MySqlCommand(selectSql, connection);
        selectCmd.Parameters.AddWithValue("@fileName", fileName);
        
        var localPath = await selectCmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            Console.WriteLine($"[HathiCatalog] File not downloaded or not found: {fileName}");
            await connection.DisposeAsync();
            return (0, 0, 0);
        }
        
        // Update import status
        await UpdateImportStatusAsync(connection, fileName, "importing", null, ct);
        
        Console.WriteLine($"[HathiCatalog] Importing {fileName}...");
        
        int imported = 0, updated = 0, skipped = 0, processed = 0;
        var batch = new List<HathiCatalogRow>();
        const int batchSize = 1000; // Smaller batches for reliability
        
        try
        {
            // If full load and NOT resuming, truncate and disable indexes
            if (isFullLoad && !resumeImport)
            {
                Console.WriteLine("[HathiCatalog] Full load - truncating table and disabling indexes for speed");
                await using var setupCmd = new MySqlCommand(@"
                    SET FOREIGN_KEY_CHECKS = 0;
                    SET UNIQUE_CHECKS = 0;
                    TRUNCATE TABLE hathi_catalog;
                    ALTER TABLE hathi_catalog DISABLE KEYS;
                ", connection);
                setupCmd.CommandTimeout = 120;
                await setupCmd.ExecuteNonQueryAsync(ct);
            }
            else if (resumeImport)
            {
                Console.WriteLine("[HathiCatalog] Resume mode - will use INSERT IGNORE to skip existing records");
            }
            
            // Open gzipped file
            await using var fileStream = File.OpenRead(localPath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);
            
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (ct.IsCancellationRequested) break;
                
                var row = ParseLine(line, fileName);
                if (row == null)
                {
                    skipped++;
                    continue;
                }
                
                batch.Add(row);
                processed++;
                
                if (batch.Count >= batchSize)
                {
                    // Reconnect if connection is broken - create new connection
                    if (connection.State != System.Data.ConnectionState.Open)
                    {
                        Console.WriteLine("[HathiCatalog] Connection lost, creating new connection...");
                        try { await connection.DisposeAsync(); } catch { }
                        connection = new MySqlConnection(_connectionString);
                        await connection.OpenAsync(ct);
                    }
                    
                    try
                    {
                        var (batchImported, batchUpdated) = await InsertBatchAsync(connection, batch, isFullLoad || resumeImport, ct);
                        imported += batchImported;
                        updated += batchUpdated;
                    }
                    catch (MySqlException ex) when (ex.Message.Contains("Connection") || ex.Message.Contains("Broken"))
                    {
                        // Connection error - retry with new connection
                        Console.WriteLine($"[HathiCatalog] Connection error, retrying batch: {ex.Message}");
                        try { await connection.DisposeAsync(); } catch { }
                        connection = new MySqlConnection(_connectionString);
                        await connection.OpenAsync(ct);
                        var (batchImported, batchUpdated) = await InsertBatchAsync(connection, batch, isFullLoad || resumeImport, ct);
                        imported += batchImported;
                        updated += batchUpdated;
                    }
                    batch.Clear();
                    
                    progress?.Report((processed, imported));
                    
                    if (processed % 100000 == 0)
                    {
                        Console.WriteLine($"[HathiCatalog] Processed {processed:N0} rows, imported {imported:N0}, updated {updated:N0}");
                    }
                }
            }
            
            // Final batch
            if (batch.Count > 0)
            {
                var (batchImported, batchUpdated) = await InsertBatchAsync(connection, batch, isFullLoad || resumeImport, ct);
                imported += batchImported;
                updated += batchUpdated;
            }
            
            // Re-enable indexes if we disabled them
            if (isFullLoad)
            {
                Console.WriteLine("[HathiCatalog] Re-enabling indexes...");
                await using var cleanupCmd = new MySqlCommand(@"
                    ALTER TABLE hathi_catalog ENABLE KEYS;
                    SET UNIQUE_CHECKS = 1;
                    SET FOREIGN_KEY_CHECKS = 1;
                ", connection);
                cleanupCmd.CommandTimeout = 600; // Index rebuild can take time
                await cleanupCmd.ExecuteNonQueryAsync(ct);
                Console.WriteLine("[HathiCatalog] Indexes rebuilt");
            }
            
            // Update import status
            await UpdateImportStatusAsync(connection, fileName, "completed", null, ct);
            await UpdateImportStatsAsync(connection, fileName, imported, updated, skipped, ct);
            
            Console.WriteLine($"[HathiCatalog] Import complete: {imported:N0} imported, {updated:N0} updated, {skipped:N0} skipped");
            await connection.DisposeAsync();
            return (imported, updated, skipped);
        }
        catch (Exception ex)
        {
            try { await UpdateImportStatusAsync(connection, fileName, "failed", ex.Message, ct); } catch { }
            try { await connection.DisposeAsync(); } catch { }
            Console.WriteLine($"[HathiCatalog] Import failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Gets pending files that need to be downloaded.
    /// </summary>
    public async Task<List<(string fileName, string fileType, DateTime fileDate)>> GetPendingDownloadsAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        const string sql = @"
            SELECT file_name, file_type, file_date 
            FROM hathi_file_list 
            WHERE download_status = 'pending'
            ORDER BY file_date DESC";
        
        await using var cmd = new MySqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        
        var files = new List<(string, string, DateTime)>();
        while (await reader.ReadAsync(ct))
        {
            files.Add((reader.GetString(0), reader.GetString(1), reader.GetDateTime(2)));
        }
        return files;
    }

    /// <summary>
    /// Gets downloaded files that need to be imported.
    /// </summary>
    public async Task<List<(string fileName, string fileType, DateTime fileDate)>> GetPendingImportsAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        // Full files first, then updates in order
        const string sql = @"
            SELECT file_name, file_type, file_date 
            FROM hathi_file_list 
            WHERE download_status = 'completed' AND import_status = 'pending'
            ORDER BY file_type DESC, file_date ASC";
        
        await using var cmd = new MySqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        
        var files = new List<(string, string, DateTime)>();
        while (await reader.ReadAsync(ct))
        {
            files.Add((reader.GetString(0), reader.GetString(1), reader.GetDateTime(2)));
        }
        return files;
    }

    /// <summary>
    /// Gets the latest full file name from the file list.
    /// </summary>
    public async Task<string?> GetLatestFullFileAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        const string sql = @"
            SELECT file_name FROM hathi_file_list 
            WHERE file_type = 'full' 
            ORDER BY file_date DESC LIMIT 1";
        
        await using var cmd = new MySqlCommand(sql, connection);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static string DetermineFileType(string filename)
    {
        if (filename.StartsWith("hathi_full_")) return "full";
        if (filename.StartsWith("hathi_upd_")) return "update";
        if (filename.Contains("header")) return "header";
        return "update";
    }

    private static DateTime ParseFileDate(string filename)
    {
        // hathi_full_20251201.txt.gz -> 2025-12-01
        // hathi_upd_20251210.txt.gz -> 2025-12-10
        try
        {
            var parts = filename.Replace("hathi_full_", "").Replace("hathi_upd_", "").Split('.')[0];
            if (parts.Length >= 8)
            {
                return DateTime.ParseExact(parts.Substring(0, 8), "yyyyMMdd", null);
            }
        }
        catch { }
        return DateTime.Today;
    }

    private HathiCatalogRow? ParseLine(string line, string sourceFile)
    {
        // Tab-separated values
        var fields = line.Split('\t');
        if (fields.Length < 12) return null; // Need at least htid through title
        
        try
        {
            return new HathiCatalogRow
            {
                Htid = fields[0],
                Access = fields[1] == "allow" ? "allow" : "deny",
                Rights = GetField(fields, 2),
                HtBibKey = long.TryParse(GetField(fields, 3), out var bibKey) ? bibKey : null,
                Description = GetField(fields, 4),
                Source = GetField(fields, 5),
                SourceBibNum = GetField(fields, 6),
                OclcNum = GetField(fields, 7),
                Isbn = GetField(fields, 8),
                Issn = GetField(fields, 9),
                Lccn = GetField(fields, 10),
                Title = GetField(fields, 11),
                Imprint = GetField(fields, 12),
                RightsReasonCode = GetField(fields, 13),
                RightsTimestamp = GetField(fields, 14),
                UsGovDocFlag = GetField(fields, 15) == "1",
                RightsDateUsed = GetField(fields, 16),
                PubPlace = GetField(fields, 17),
                Lang = GetField(fields, 18),
                BibFmt = GetField(fields, 19),
                CollectionCode = GetField(fields, 20),
                ContentProviderCode = GetField(fields, 21),
                ResponsibleEntityCode = GetField(fields, 22),
                DigitizationAgentCode = GetField(fields, 23),
                AccessProfileCode = GetField(fields, 24),
                Author = GetField(fields, 25),
                SourceFile = sourceFile
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetField(string[] fields, int index)
    {
        if (index >= fields.Length) return null;
        var value = fields[index];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string EscapeSql(string? value)
    {
        if (value == null) return "NULL";
        // Escape single quotes and backslashes for SQL
        return "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    }

    private async Task<(int imported, int updated)> InsertBatchAsync(
        MySqlConnection connection, 
        List<HathiCatalogRow> batch,
        bool useInsertIgnore,
        CancellationToken ct)
    {
        if (batch.Count == 0) return (0, 0);
        
        // Build a multi-row INSERT statement for much better performance
        // Use INSERT IGNORE for full load/resume to skip duplicates silently
        var sb = new System.Text.StringBuilder();
        var insertCmd = useInsertIgnore ? "INSERT IGNORE INTO" : "INSERT INTO";
        sb.AppendLine($@"{insertCmd} hathi_catalog (
            htid, access, rights, ht_bib_key, description, source, source_bib_num,
            oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code,
            rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang,
            bib_fmt, collection_code, content_provider_code, responsible_entity_code,
            digitization_agent_code, access_profile_code, author, source_file
        ) VALUES ");

        for (int i = 0; i < batch.Count; i++)
        {
            var row = batch[i];
            if (i > 0) sb.Append(",");
            sb.Append("(");
            sb.Append(EscapeSql(row.Htid)); sb.Append(",");
            sb.Append(EscapeSql(row.Access)); sb.Append(",");
            sb.Append(EscapeSql(row.Rights)); sb.Append(",");
            sb.Append(row.HtBibKey?.ToString() ?? "NULL"); sb.Append(",");
            sb.Append(EscapeSql(row.Description)); sb.Append(",");
            sb.Append(EscapeSql(row.Source)); sb.Append(",");
            sb.Append(EscapeSql(row.SourceBibNum)); sb.Append(",");
            sb.Append(EscapeSql(row.OclcNum)); sb.Append(",");
            sb.Append(EscapeSql(row.Isbn)); sb.Append(",");
            sb.Append(EscapeSql(row.Issn)); sb.Append(",");
            sb.Append(EscapeSql(row.Lccn)); sb.Append(",");
            sb.Append(EscapeSql(row.Title)); sb.Append(",");
            sb.Append(EscapeSql(row.Imprint)); sb.Append(",");
            sb.Append(EscapeSql(row.RightsReasonCode)); sb.Append(",");
            sb.Append(EscapeSql(row.RightsTimestamp)); sb.Append(",");
            sb.Append(row.UsGovDocFlag ? "1" : "0"); sb.Append(",");
            sb.Append(EscapeSql(row.RightsDateUsed)); sb.Append(",");
            sb.Append(EscapeSql(row.PubPlace)); sb.Append(",");
            sb.Append(EscapeSql(row.Lang)); sb.Append(",");
            sb.Append(EscapeSql(row.BibFmt)); sb.Append(",");
            sb.Append(EscapeSql(row.CollectionCode)); sb.Append(",");
            sb.Append(EscapeSql(row.ContentProviderCode)); sb.Append(",");
            sb.Append(EscapeSql(row.ResponsibleEntityCode)); sb.Append(",");
            sb.Append(EscapeSql(row.DigitizationAgentCode)); sb.Append(",");
            sb.Append(EscapeSql(row.AccessProfileCode)); sb.Append(",");
            sb.Append(EscapeSql(row.Author)); sb.Append(",");
            sb.Append(EscapeSql(row.SourceFile));
            sb.Append(")");
        }

        // For full load or resume (using INSERT IGNORE), skip duplicate handling for speed
        // For regular updates, use ON DUPLICATE KEY UPDATE for upsert
        if (!useInsertIgnore)
        {
            sb.AppendLine(@" ON DUPLICATE KEY UPDATE
                access = VALUES(access),
                rights = VALUES(rights),
                title = VALUES(title),
                author = VALUES(author),
                oclc_num = VALUES(oclc_num),
                isbn = VALUES(isbn),
                source_file = VALUES(source_file),
                updated_at = CURRENT_TIMESTAMP");
        }

        await using var cmd = new MySqlCommand(sb.ToString(), connection);
        cmd.CommandTimeout = 600; // 10 minutes for large batches
        
        var result = await cmd.ExecuteNonQueryAsync(ct);
        
        // For multi-row insert, result is total affected rows
        int imported = result;
        int updated = 0;
        
        return (imported, updated);
    }

    private async Task UpdateDownloadStatusAsync(MySqlConnection connection, string fileName, 
        string status, string? localPath, string? error, CancellationToken ct)
    {
        var sql = status switch
        {
            "downloading" => "UPDATE hathi_file_list SET download_status = @status, download_started_at = NOW() WHERE file_name = @fileName",
            "completed" => "UPDATE hathi_file_list SET download_status = @status, download_completed_at = NOW(), local_path = @localPath WHERE file_name = @fileName",
            "failed" => "UPDATE hathi_file_list SET download_status = @status, download_error = @error WHERE file_name = @fileName",
            _ => "UPDATE hathi_file_list SET download_status = @status WHERE file_name = @fileName"
        };
        
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        cmd.Parameters.AddWithValue("@localPath", localPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@error", error ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateImportStatusAsync(MySqlConnection connection, string fileName,
        string status, string? error, CancellationToken ct)
    {
        var sql = status switch
        {
            "importing" => "UPDATE hathi_file_list SET import_status = @status, import_started_at = NOW() WHERE file_name = @fileName",
            "completed" => "UPDATE hathi_file_list SET import_status = @status, import_completed_at = NOW() WHERE file_name = @fileName",
            "failed" => "UPDATE hathi_file_list SET import_status = @status, import_error = @error WHERE file_name = @fileName",
            _ => "UPDATE hathi_file_list SET import_status = @status WHERE file_name = @fileName"
        };
        
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        cmd.Parameters.AddWithValue("@error", error ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateImportStatsAsync(MySqlConnection connection, string fileName,
        int imported, int updated, int skipped, CancellationToken ct)
    {
        const string sql = @"
            UPDATE hathi_file_list 
            SET rows_imported = @imported, rows_updated = @updated, rows_skipped = @skipped 
            WHERE file_name = @fileName";
        
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@imported", imported);
        cmd.Parameters.AddWithValue("@updated", updated);
        cmd.Parameters.AddWithValue("@skipped", skipped);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private class HathiFileInfo
    {
        public string Filename { get; set; } = "";
        public long Size { get; set; }
    }

    private class HathiCatalogRow
    {
        public string Htid { get; set; } = "";
        public string Access { get; set; } = "deny";
        public string? Rights { get; set; }
        public long? HtBibKey { get; set; }
        public string? Description { get; set; }
        public string? Source { get; set; }
        public string? SourceBibNum { get; set; }
        public string? OclcNum { get; set; }
        public string? Isbn { get; set; }
        public string? Issn { get; set; }
        public string? Lccn { get; set; }
        public string? Title { get; set; }
        public string? Imprint { get; set; }
        public string? RightsReasonCode { get; set; }
        public string? RightsTimestamp { get; set; }
        public bool UsGovDocFlag { get; set; }
        public string? RightsDateUsed { get; set; }
        public string? PubPlace { get; set; }
        public string? Lang { get; set; }
        public string? BibFmt { get; set; }
        public string? CollectionCode { get; set; }
        public string? ContentProviderCode { get; set; }
        public string? ResponsibleEntityCode { get; set; }
        public string? DigitizationAgentCode { get; set; }
        public string? AccessProfileCode { get; set; }
        public string? Author { get; set; }
        public string? SourceFile { get; set; }
    }
}
