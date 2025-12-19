#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

using var command = new MySqlCommand("SELECT Title, Author, SourceId, SourceIdType FROM bookcityfile LIMIT 10", connection);
using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"Title: {reader["Title"]}, Author: {reader["Author"]}, SourceId: {reader["SourceId"]}, Type: {reader["SourceIdType"]}");
}
