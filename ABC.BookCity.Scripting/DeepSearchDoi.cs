#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";
var targetDoi = "10.1021/acs.chemmater.0c04689";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine($"--- Searching for DOI: {targetDoi} ---");

    // 1. Check libgenrs_updated (it has a Doi column)
    try {
        using var cmd = new MySqlCommand("SELECT COUNT(*) FROM libgenrs_updated WHERE Doi = @doi", connection);
        cmd.Parameters.AddWithValue("@doi", targetDoi);
        var count = await cmd.ExecuteScalarAsync();
        Console.WriteLine($"libgenrs_updated: {count} matches");
    } catch (Exception ex) { Console.WriteLine($"libgenrs_updated error: {ex.Message}"); }

    // 2. Check if there are any other tables with 'scimag' in the name
    Console.WriteLine("\n--- Tables with 'scimag' in name ---");
    using var cmdTables = new MySqlCommand("SHOW TABLES LIKE '%scimag%'", connection);
    using var readerTables = await cmdTables.ExecuteReaderAsync();
    var scimagTables = new List<string>();
    while (await readerTables.ReadAsync()) {
        scimagTables.Add(readerTables.GetString(0));
    }
    await readerTables.CloseAsync();
    foreach (var t in scimagTables) Console.WriteLine($"- {t}");

    // 3. Check for tables with 'doi' column that aren't crashed
    Console.WriteLine("\n--- Non-crashed tables with 'doi' column ---");
    var sql = @"
        SELECT TABLE_NAME, COLUMN_NAME 
        FROM information_schema.COLUMNS 
        WHERE COLUMN_NAME LIKE '%doi%' 
        AND TABLE_SCHEMA = 'allthethings'";
    using var cmdDoi = new MySqlCommand(sql, connection);
    using var readerDoi = await cmdDoi.ExecuteReaderAsync();
    var doiCols = new List<(string Table, string Col)>();
    while (await readerDoi.ReadAsync()) {
        doiCols.Add((readerDoi.GetString(0), readerDoi.GetString(1)));
    }
    await readerDoi.CloseAsync();

    foreach (var item in doiCols) {
        try {
            using var cmdCheck = new MySqlCommand($"SELECT COUNT(*) FROM `{item.Table}` WHERE `{item.Col}` = @doi", connection);
            cmdCheck.Parameters.AddWithValue("@doi", targetDoi);
            var count = await cmdCheck.ExecuteScalarAsync();
            Console.WriteLine($"{item.Table}.{item.Col}: {count} matches");
        } catch (Exception ex) {
            Console.WriteLine($"{item.Table}.{item.Col}: Error ({ex.Message})");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
