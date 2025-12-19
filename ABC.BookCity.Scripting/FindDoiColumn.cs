#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    using var cmd = new MySqlCommand(@"
        SELECT TABLE_NAME, COLUMN_NAME 
        FROM INFORMATION_SCHEMA.COLUMNS 
        WHERE COLUMN_NAME LIKE '%doi%' 
        AND TABLE_SCHEMA = 'allthethings'", connection);
        
    using var reader = await cmd.ExecuteReaderAsync();
    Console.WriteLine("Tables with DOI-like columns:");
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"- {reader.GetString(0)}.{reader.GetString(1)}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
