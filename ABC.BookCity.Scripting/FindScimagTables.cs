#:package MySqlConnector@2.3.7

using MySqlConnector;

var connectionString = "Server=localhost;Port=3306;User=root;Password=password;";

try
{
    using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    
    Console.WriteLine("--- Listing ALL tables in allthethings ---");
    using var cmd = new MySqlCommand(@"
        SELECT TABLE_NAME 
        FROM information_schema.TABLES 
        WHERE TABLE_SCHEMA = 'allthethings'
        ORDER BY TABLE_NAME", connection);
    
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"- {reader.GetString(0)}");
    }
    await reader.CloseAsync();

    /*
    Console.WriteLine("\n--- Checking for 'scimag' columns ---");
    */
    using var cmdCol = new MySqlCommand(@"
        SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME 
        FROM information_schema.COLUMNS 
        WHERE COLUMN_NAME LIKE '%scimag%'", connection);
    
    using var readerCol = await cmdCol.ExecuteReaderAsync();
    while (await readerCol.ReadAsync())
    {
        Console.WriteLine($"DB: {readerCol.GetString(0)} | Table: {readerCol.GetString(1)} | Column: {readerCol.GetString(2)}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
