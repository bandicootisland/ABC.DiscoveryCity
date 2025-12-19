#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("Searching for tables with 'doi' columns...");
    
    var sql = @"
        SELECT TABLE_NAME, COLUMN_NAME 
        FROM information_schema.COLUMNS 
        WHERE COLUMN_NAME LIKE '%doi%' 
        AND TABLE_SCHEMA = 'allthethings'";

    using var cmd = new MySqlCommand(sql, connection);
    using var reader = await cmd.ExecuteReaderAsync();
    
    var tablesWithDoi = new List<(string Table, string Column)>();
    while (await reader.ReadAsync())
    {
        tablesWithDoi.Add((reader.GetString(0), reader.GetString(1)));
    }
    await reader.CloseAsync();

    foreach (var item in tablesWithDoi)
    {
        Console.WriteLine($"Checking table: {item.Table} (Column: {item.Column})");
        try {
            using var sampleCmd = new MySqlCommand($"SELECT `{item.Column}` FROM `{item.Table}` WHERE `{item.Column}` IS NOT NULL AND `{item.Column}` != '' LIMIT 3", connection);
            using var sampleReader = await sampleCmd.ExecuteReaderAsync();
            while (await sampleReader.ReadAsync())
            {
                Console.WriteLine($"  Sample: {sampleReader.GetValue(0)}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"  Error sampling: {ex.Message}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
