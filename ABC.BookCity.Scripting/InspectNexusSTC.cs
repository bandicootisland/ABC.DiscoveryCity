#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

Console.WriteLine("Columns in annas_archive_meta__aacid__nexusstc_records:");
using var command = new MySqlCommand("DESC annas_archive_meta__aacid__nexusstc_records", connection);
using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader["Field"]} ({reader["Type"]})");
}
