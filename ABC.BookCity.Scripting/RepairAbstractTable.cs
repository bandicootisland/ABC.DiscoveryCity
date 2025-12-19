#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings;Command Timeout=0";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

Console.WriteLine("Repairing libgenli_editions_add_descr...");
using var command = new MySqlCommand("REPAIR TABLE libgenli_editions_add_descr", connection);
using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader[0]} | {reader[1]} | {reader[2]} | {reader[3]}");
}
