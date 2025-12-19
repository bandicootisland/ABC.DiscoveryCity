#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("Sample records from annas_archive_meta__aacid__nexusstc_records:");
    using var cmd = new MySqlCommand("SELECT primary_id, md5 FROM annas_archive_meta__aacid__nexusstc_records LIMIT 10", connection);
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"ID: {reader.GetValue(0)} | MD5: {reader.GetValue(1)}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
