#:package MySqlConnector@2.3.5

using System;
using MySqlConnector;

var connectionString = "Server=localhost;User ID=root;Password=password;Database=allthethings";
var doi = "10.1021/acs.chemmater.0c04689";

using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

Console.WriteLine($"Searching database for DOI: {doi}");

// 1. Find the file in libgenli_files to get the edition_id or md5
string query = @"
    SELECT f.f_id, f.md5, e.descr 
    FROM libgenli_files f
    LEFT JOIN libgenli_editions_add_descr e ON f.f_id = e.id
    WHERE f.doi = @doi";

using var command = new MySqlCommand(query, connection);
command.Parameters.AddWithValue("@doi", doi);

using var reader = await command.ExecuteReaderAsync();
if (await reader.ReadAsync())
{
    var md5 = reader["md5"]?.ToString();
    var descr = reader["descr"]?.ToString();
    
    Console.WriteLine($"MD5: {md5}");
    Console.WriteLine($"Abstract (libgenli): {(string.IsNullOrEmpty(descr) ? "N/A" : descr.Substring(0, Math.Min(200, descr.Length)) + "...")}");
}
else
{
    Console.WriteLine("DOI not found in libgenli_files.");
}
connection.Close();
await connection.OpenAsync();

// 2. Try libgenrs_description via MD5 if we have it
// (Need to get MD5 first if not found above)
if (true) {
    using var cmd2 = new MySqlCommand("SELECT descr FROM libgenrs_description WHERE md5 = (SELECT md5 FROM libgenli_files WHERE doi = @doi LIMIT 1)", connection);
    cmd2.Parameters.AddWithValue("@doi", doi);
    var rsDescr = await cmd2.ExecuteScalarAsync();
    Console.WriteLine($"Abstract (libgenrs): {(rsDescr == null || rsDescr == DBNull.Value ? "N/A" : rsDescr.ToString().Substring(0, Math.Min(200, rsDescr.ToString().Length)) + "...")}");
}
