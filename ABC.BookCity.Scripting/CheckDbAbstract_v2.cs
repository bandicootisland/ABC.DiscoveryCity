#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";
var doi = "10.1021/acs.chemmater.0c04689";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

Console.WriteLine($"Searching for MD5 for DOI: {doi}");

// 1. Find MD5 from libgenli_files
string md5 = null;
using (var cmd = new MySqlCommand("SELECT md5 FROM libgenli_files WHERE doi = @doi LIMIT 1", connection))
{
    cmd.Parameters.AddWithValue("@doi", doi);
    md5 = (string)await cmd.ExecuteScalarAsync();
}

if (string.IsNullOrEmpty(md5))
{
    Console.WriteLine("MD5 not found in libgenli_files.");
}
else
{
    Console.WriteLine($"Found MD5: {md5}");
    
    // 2. Check libgenrs_description
    using (var cmd = new MySqlCommand("SELECT descr FROM libgenrs_description WHERE md5 = @md5", connection))
    {
        cmd.Parameters.AddWithValue("@md5", md5);
        var descr = (string)await cmd.ExecuteScalarAsync();
        if (!string.IsNullOrEmpty(descr))
        {
            Console.WriteLine("\n--- ABSTRACT FOUND IN libgenrs_description ---");
            Console.WriteLine(descr);
        }
        else
        {
            Console.WriteLine("Abstract not found in libgenrs_description.");
        }
    }
}
