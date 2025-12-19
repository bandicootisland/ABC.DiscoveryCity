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
    
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"Table: {reader.GetString(0)} | Column: {reader.GetString(1)}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
