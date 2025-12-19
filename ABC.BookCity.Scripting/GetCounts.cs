#:package MySqlConnector@2.3.7
using MySqlConnector;
var connectionString = "Server=localhost;Port=3306;User=root;Password=password;Database=allthethings;";
using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();
using var cmd = new MySqlCommand("SELECT TABLE_NAME, TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'allthethings' AND TABLE_NAME IN ('libgenli_files', 'libgenli_editions', 'scihub_dois')", connection);
using var reader = await cmd.ExecuteReaderAsync();
while (await reader.ReadAsync()) Console.WriteLine($"{reader.GetString(0)}: {reader["TABLE_ROWS"]}");
