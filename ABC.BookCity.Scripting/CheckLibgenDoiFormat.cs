#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("Searching for DOIs in libgenrs_updated that look like the zip files...");
    
    // The zip files have DOIs like 10.1021/...
    // Let's see what the first few DOIs in the table look like
    var sql = "SELECT Doi, Title FROM libgenrs_updated WHERE Doi IS NOT NULL AND Doi != '' LIMIT 20";

    using var cmd = new MySqlCommand(sql, connection);
    using var reader = await cmd.ExecuteReaderAsync();
    
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"Doi: {reader.GetString(0)} | Title: {reader.GetString(1)}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
