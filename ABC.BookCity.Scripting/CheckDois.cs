#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";
var dois = new[] { 
    "10.1021/acs.chemmater.0c04689", 
    "10.1021/acs.chemmater.0c04708", 
    "10.1021/acs.chemmater.0c04710" 
};

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

foreach (var doi in dois)
{
    using var command = new MySqlCommand("SELECT doi, title FROM scihub_dois WHERE doi = @doi", connection);
    command.Parameters.AddWithValue("@doi", doi);
    using var reader = await command.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        Console.WriteLine($"FOUND: {doi} - {reader["title"]}");
    }
    else
    {
        Console.WriteLine($"NOT FOUND: {doi}");
    }
}
