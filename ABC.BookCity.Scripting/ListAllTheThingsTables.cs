#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

Console.WriteLine("Tables in allthethings:");
using var command = new MySqlCommand("SHOW TABLES", connection);
using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine(reader[0]);
}
