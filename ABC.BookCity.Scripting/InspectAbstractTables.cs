#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

var tables = new[] { "libgenli_editions_add_descr", "libgenrs_description" };

foreach (var table in tables)
{
    Console.WriteLine($"\nColumns in {table}:");
    try {
        using var command = new MySqlCommand($"DESC {table}", connection);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader["Field"]} ({reader["Type"]})");
        }
    } catch (Exception ex) {
        Console.WriteLine($"Error: {ex.Message}");
    }
}
