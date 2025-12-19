#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";
var targetDoi = "10.1021/acs.chemmater.0c04689";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("\n--- Searching for DOI suffix '0c04689' in libgenrs_updated ---");
    try {
        using var cmd = new MySqlCommand("SELECT Doi, Title FROM libgenrs_updated WHERE Doi LIKE '%0c04689%'", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"Found DOI: {reader.GetString(0)} | Title: {reader.GetString(1)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    Console.WriteLine("\n--- Searching for DOI suffix '0c04689' in scihub_dois ---");
    try {
        using var cmd = new MySqlCommand("SELECT doi FROM scihub_dois WHERE doi LIKE '%0c04689%'", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"Found DOI: {reader.GetString(0)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    Console.WriteLine("\n--- Checking columns of libgenli_editions via information_schema ---");
    try {
        using var cmd = new MySqlCommand("SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_NAME = 'libgenli_editions' AND TABLE_SCHEMA = 'allthethings'", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    Console.WriteLine("\n--- Checking columns of libgenli_files via information_schema ---");
    try {
        using var cmd = new MySqlCommand("SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_NAME = 'libgenli_files' AND TABLE_SCHEMA = 'allthethings'", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    Console.WriteLine("\n--- Searching for exact DOI in scihub_dois ---");
    try {
        using var cmd = new MySqlCommand("SELECT doi FROM scihub_dois WHERE doi = @doi", connection);
        cmd.Parameters.AddWithValue("@doi", "10.1021/acs.chemmater.0c04689");
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) {
            Console.WriteLine($"Found DOI in scihub_dois: {reader.GetString(0)}");
        } else {
            Console.WriteLine("DOI not found in scihub_dois");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    Console.WriteLine("\n--- Checking for any DOI starting with 10.1021 in scihub_dois ---");
    try {
        using var cmd = new MySqlCommand("SELECT doi FROM scihub_dois WHERE doi LIKE '10.1021/%' LIMIT 5", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"Found DOI: {reader.GetString(0)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    // 3. Try libgenli_files (even if it might be crashed, let's see)
    Console.WriteLine("\n--- Searching for DOI in libgenli_files.scimag_archive_path ---");
    try
    {
        using var cmd3 = new MySqlCommand("SELECT f_id, scimag_id, scimag_archive_path FROM libgenli_files WHERE scimag_archive_path LIKE @doi", connection);
        cmd3.Parameters.AddWithValue("@doi", "%" + targetDoi + "%");
        using var reader3 = await cmd3.ExecuteReaderAsync();
        while (await reader3.ReadAsync())
        {
            Console.WriteLine($"Found in libgenli_files: f_id={reader3["f_id"]}, scimag_id={reader3["scimag_id"]}, path={reader3["scimag_archive_path"]}");
        }
    }
    catch (Exception ex) { Console.WriteLine("libgenli_files query failed: " + ex.Message); }

    // 4. Try libgenli_editions
    Console.WriteLine("\n--- Searching for DOI in libgenli_editions.doi ---");
    try
    {
        using var cmd4 = new MySqlCommand("SELECT e_id, title, doi FROM libgenli_editions WHERE doi = @doi", connection);
        cmd4.Parameters.AddWithValue("@doi", targetDoi);
        using var reader4 = await cmd4.ExecuteReaderAsync();
        while (await reader4.ReadAsync())
        {
            Console.WriteLine($"Found in libgenli_editions: e_id={reader4["e_id"]}, title={reader4["title"]}, doi={reader4["doi"]}");
        }
    }
    catch (Exception ex) { Console.WriteLine("libgenli_editions query failed: " + ex.Message); }

    // 5. Look for scimag_id range
    Console.WriteLine("\n--- Looking for scimag_id range 87500000-87500100 in libgenli_files ---");
    try
    {
        using var cmd5 = new MySqlCommand("SELECT f_id, scimag_id, scimag_archive_path FROM libgenli_files WHERE scimag_id BETWEEN 87500000 AND 87500100 LIMIT 10", connection);
        using var reader5 = await cmd5.ExecuteReaderAsync();
        while (await reader5.ReadAsync())
        {
            Console.WriteLine($"scimag_id={reader5["scimag_id"]}, path={reader5["scimag_archive_path"]}");
        }
    }
    catch (Exception ex) { Console.WriteLine("libgenli_files range query failed: " + ex.Message); }

    Console.WriteLine("\n--- Checking counts via information_schema (fast) ---");
    try {
        string[] tables = { "libgenli_files", "libgenli_editions", "scihub_dois", "annas_archive_meta__aacid__nexusstc_records" };
        string tableList = "'" + string.Join("','", tables) + "'";
        using var cmd = new MySqlCommand($"SELECT TABLE_NAME, TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'allthethings' AND TABLE_NAME IN ({tableList})", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"{reader.GetString(0)}: {reader["TABLE_ROWS"]} rows");
        }
    } catch (Exception ex) { Console.WriteLine($"Count query failed: {ex.Message}"); }

    Console.WriteLine("\n--- Checking columns of annas_archive_meta__aacid__nexusstc_records ---");
    try {
        using var cmd = new MySqlCommand("SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_NAME = 'annas_archive_meta__aacid__nexusstc_records' AND TABLE_SCHEMA = 'allthethings'", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
