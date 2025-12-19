#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";
var targetId = 87500000;
var targetDoi = "10.1021/acs.chemmater.0c04689";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("--- Attempting to repair scihub_dois ---");
    try {
        using var cmdRepair = new MySqlCommand("REPAIR TABLE scihub_dois", connection);
        using var readerRepair = await cmdRepair.ExecuteReaderAsync();
        while (await readerRepair.ReadAsync()) {
            Console.WriteLine($"{readerRepair.GetValue(2)}: {readerRepair.GetValue(3)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Repair failed: {ex.Message}"); }

    Console.WriteLine($"\n--- Searching for ID: {targetId} ---");
    var tablesWithId = new[] { "libgenrs_updated", "libgenli_files", "libgenli_editions" };
    foreach (var table in tablesWithId) {
        try {
            using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{table}` WHERE ID = @id", connection);
            cmd.Parameters.AddWithValue("@id", targetId);
            var count = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"{table}: {count} matches for ID {targetId}");
        } catch (Exception ex) { Console.WriteLine($"{table}: {ex.Message}"); }
    }

    Console.WriteLine("\n--- Checking for any DOIs starting with 10.1021 in libgenrs_updated ---");
    try {
        using var cmd = new MySqlCommand("SELECT Doi FROM libgenrs_updated WHERE Doi LIKE '10.1021/%' LIMIT 5", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"Found DOI: {reader.GetString(0)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }

    Console.WriteLine("\n--- Checking for any DOIs starting with 10.1021 in scihub_dois ---");
    try {
        using var cmd = new MySqlCommand("SELECT doi FROM scihub_dois WHERE doi LIKE '10.1021/%' LIMIT 5", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            Console.WriteLine($"Found DOI: {reader.GetString(0)}");
        }
    } catch (Exception ex) { Console.WriteLine($"Search failed: {ex.Message}"); }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
