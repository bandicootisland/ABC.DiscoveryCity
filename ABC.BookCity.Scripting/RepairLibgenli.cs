#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("Repairing libgenli_files...");
    using (var cmd = new MySqlCommand("REPAIR TABLE libgenli_files", connection))
    {
        cmd.CommandTimeout = 0; // No timeout for repair
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetString(3)}");
        }
    }

    Console.WriteLine("\nRepairing libgenli_editions...");
    using (var cmd = new MySqlCommand("REPAIR TABLE libgenli_editions", connection))
    {
        cmd.CommandTimeout = 0;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetString(3)}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
