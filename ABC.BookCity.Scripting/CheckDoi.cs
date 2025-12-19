#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";
var doi = "10.1021/acs.chemmater.0c04689";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine($"Checking for DOI: {doi}");
    
    var id = 87500000;
    var tables = new[] { "aarecords_all_md5" };
    foreach (var table in tables)
    {
        try {
            // Check if there is a column that might contain DOI or ID
            using var cmd = new MySqlCommand($"SELECT * FROM {table} LIMIT 1", connection);
            using var reader = await cmd.ExecuteReaderAsync();
            Console.WriteLine($"\nColumns in {table}:");
            for (int i = 0; i < reader.FieldCount; i++) {
                Console.Write($"{reader.GetName(i)}, ");
            }
            Console.WriteLine();
        } catch (Exception ex) {
            Console.WriteLine($"Table {table} error: {ex.Message}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
