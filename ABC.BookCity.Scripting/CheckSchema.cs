#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";
var tables = new[] { "libgenli_editions" };

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    foreach (var table in tables)
    {
        Console.WriteLine($"\n--- Schema for {table} ---");
        using var cmd = new MySqlCommand($"DESCRIBE {table}", connection);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
