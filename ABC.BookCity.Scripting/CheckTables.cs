#:package MySqlConnector@2.3.7
#:package System.Text.Json@8.0.0


using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection("Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;");
    await connection.OpenAsync();
    Console.WriteLine("Connected to MariaDB (allthethings)");

    using var cmd = new MySqlCommand("SHOW TABLES LIKE '%scimag%'", connection);
    using var reader = await cmd.ExecuteReaderAsync();
    
    Console.WriteLine("Tables containing 'scimag':");
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"- {reader.GetString(0)}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
