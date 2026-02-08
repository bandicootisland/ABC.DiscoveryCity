using System.Text.Json;

namespace ABC.DiscoveryCity.Importer;

public class ImportHistory
{
    private readonly string _historyFilePath;
    private HashSet<string> _importedFiles;

    public ImportHistory(string historyFilePath)
    {
        _historyFilePath = historyFilePath;
        _importedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Load();
    }

    private void Load()
    {
        if (File.Exists(_historyFilePath))
        {
            try
            {
                var json = File.ReadAllText(_historyFilePath);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    _importedFiles = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Ignore errors, start fresh
            }
        }
    }

    public void MarkAsImported(string fileName)
    {
        if (_importedFiles.Add(fileName))
        {
            Save();
        }
    }

    public bool IsImported(string fileName)
    {
        return _importedFiles.Contains(fileName);
    }

    private void Save()
    {
        var list = _importedFiles.ToList();
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_historyFilePath, json);
    }
}
