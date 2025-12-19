#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

using var command = new MySqlCommand("SELECT COUNT(*) FROM bookcityfile", connection);
var count = await command.ExecuteScalarAsync();
Console.WriteLine($"Total records in bookcityfile: {count}");
