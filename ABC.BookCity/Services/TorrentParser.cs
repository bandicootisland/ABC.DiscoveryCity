using System.Text;

namespace ABC.BookCity.Services;

public class TorrentInfo
{
    public string? Name { get; set; }
    public string? Comment { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreationDate { get; set; }
    public List<TorrentFile> Files { get; set; } = new();
}

public class TorrentFile
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
}

public static class TorrentParser
{
    public static TorrentInfo Parse(byte[] bytes)
    {
        var info = new TorrentInfo();
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        var root = ParseElement(reader) as Dictionary<string, object>;
        if (root == null) return info;

        if (root.ContainsKey("comment") && root["comment"] is byte[] commentBytes)
            info.Comment = Encoding.UTF8.GetString(commentBytes);

        if (root.ContainsKey("created by") && root["created by"] is byte[] createdByBytes)
            info.CreatedBy = Encoding.UTF8.GetString(createdByBytes);

        if (root.ContainsKey("creation date") && root["creation date"] is long creationDate)
            info.CreationDate = DateTimeOffset.FromUnixTimeSeconds(creationDate).DateTime;

        if (root.ContainsKey("info") && root["info"] is Dictionary<string, object> infoDict)
        {
            if (infoDict.ContainsKey("name") && infoDict["name"] is byte[] nameBytes)
                info.Name = Encoding.UTF8.GetString(nameBytes);

            if (infoDict.ContainsKey("files") && infoDict["files"] is List<object> filesList)
            {
                // Multi-file mode
                foreach (var fileObj in filesList)
                {
                    if (fileObj is Dictionary<string, object> fileDict)
                    {
                        var file = new TorrentFile();
                        if (fileDict.ContainsKey("length") && fileDict["length"] is long length)
                            file.Length = length;

                        if (fileDict.ContainsKey("path") && fileDict["path"] is List<object> pathList)
                        {
                            var paths = pathList.Cast<byte[]>().Select(b => Encoding.UTF8.GetString(b));
                            file.Path = string.Join("/", paths);
                        }
                        info.Files.Add(file);
                    }
                }
            }
            else if (infoDict.ContainsKey("length") && infoDict["length"] is long length)
            {
                // Single-file mode
                info.Files.Add(new TorrentFile
                {
                    Path = info.Name ?? "Unknown",
                    Length = length
                });
            }
        }

        return info;
    }

    private static object? ParseElement(BinaryReader reader)
    {
        if (reader.BaseStream.Position >= reader.BaseStream.Length) return null;

        var b = reader.ReadByte();
        reader.BaseStream.Position--; // Peek

        if (b == 'd') return ParseDictionary(reader);
        if (b == 'l') return ParseList(reader);
        if (b == 'i') return ParseInteger(reader);
        if (char.IsDigit((char)b)) return ParseString(reader);

        return null;
    }

    private static Dictionary<string, object> ParseDictionary(BinaryReader reader)
    {
        var dict = new Dictionary<string, object>();
        reader.ReadByte(); // consume 'd'

        while (true)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length) break;
            var b = reader.ReadByte();
            reader.BaseStream.Position--; // Peek

            if (b == 'e')
            {
                reader.ReadByte(); // consume 'e'
                break;
            }

            var keyBytes = ParseString(reader);
            if (keyBytes == null) break;
            
            var key = Encoding.UTF8.GetString(keyBytes);
            var value = ParseElement(reader);

            if (value != null)
                dict[key] = value;
        }
        return dict;
    }

    private static List<object> ParseList(BinaryReader reader)
    {
        var list = new List<object>();
        reader.ReadByte(); // consume 'l'

        while (true)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length) break;
            var b = reader.ReadByte();
            reader.BaseStream.Position--; // Peek

            if (b == 'e')
            {
                reader.ReadByte(); // consume 'e'
                break;
            }

            var value = ParseElement(reader);
            if (value != null)
                list.Add(value);
        }
        return list;
    }

    private static long ParseInteger(BinaryReader reader)
    {
        reader.ReadByte(); // consume 'i'
        var sb = new StringBuilder();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 'e') break;
            sb.Append((char)b);
        }
        return long.Parse(sb.ToString());
    }

    private static byte[]? ParseString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == ':') break;
            sb.Append((char)b);
        }

        if (int.TryParse(sb.ToString(), out int length))
        {
            return reader.ReadBytes(length);
        }
        return null;
    }
}
