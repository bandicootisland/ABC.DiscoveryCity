using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    Console.WriteLine("Connected to MariaDB");

    using var cmd = new MySqlCommand("SHOW TABLES", connection);
    using var reader = await cmd.ExecuteReaderAsync();
    
    Console.WriteLine("Tables in allthethings:");
    while (await reader.ReadAsync())
    {
        var tableName = reader.GetString(0);
        if (tableName.Contains("scimag") || tableName.Contains("libgen") || tableName.Contains("doi"))
        {
            Console.WriteLine($"- {tableName}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
