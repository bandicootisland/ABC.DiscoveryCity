using System.Text.Json;
using Microsoft.JSInterop;

namespace ABC.DiscoveryCity.Services;

public class UserListService
{
    private readonly IJSRuntime _js;
    private const string StorageKey = "userLists";

    public UserListService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<List<UserList>> GetAllAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("pageState.get", StorageKey);
            if (string.IsNullOrEmpty(json)) return new();
            return JsonSerializer.Deserialize<List<UserList>>(json) ?? new();
        }
        catch { return new(); }
    }

    public async Task<UserList?> GetByIdAsync(string id)
    {
        var lists = await GetAllAsync();
        return lists.FirstOrDefault(l => l.Id == id);
    }

    public async Task SaveAsync(UserList list)
    {
        var lists = await GetAllAsync();
        var existing = lists.FindIndex(l => l.Id == list.Id);
        if (existing >= 0)
        {
            list.ModifiedAt = DateTime.UtcNow;
            lists[existing] = list;
        }
        else
        {
            lists.Add(list);
        }
        await PersistAsync(lists);
    }

    public async Task DeleteAsync(string id)
    {
        var lists = await GetAllAsync();
        lists.RemoveAll(l => l.Id == id);
        await PersistAsync(lists);
    }

    public async Task RenameAsync(string id, string newName)
    {
        var lists = await GetAllAsync();
        var list = lists.FirstOrDefault(l => l.Id == id);
        if (list != null)
        {
            list.Name = newName;
            list.ModifiedAt = DateTime.UtcNow;
            await PersistAsync(lists);
        }
    }

    /// <summary>Union of two lists (merge)</summary>
    public UserList Merge(UserList a, UserList b)
    {
        var combined = new Dictionary<string, UserListItem>();
        foreach (var item in a.Items) combined.TryAdd(item.FilePath, item);
        foreach (var item in b.Items) combined.TryAdd(item.FilePath, item);
        return new UserList
        {
            Name = $"{a.Name} + {b.Name}",
            SourceQuery = $"merge({a.Name}, {b.Name})",
            Items = combined.Values.ToList()
        };
    }

    /// <summary>Symmetric difference (items in one but not both)</summary>
    public UserList Xor(UserList a, UserList b)
    {
        var aPaths = a.Items.Select(i => i.FilePath).ToHashSet();
        var bPaths = b.Items.Select(i => i.FilePath).ToHashSet();
        var result = a.Items.Where(i => !bPaths.Contains(i.FilePath))
            .Concat(b.Items.Where(i => !aPaths.Contains(i.FilePath)))
            .ToList();
        return new UserList
        {
            Name = $"{a.Name} XOR {b.Name}",
            SourceQuery = $"xor({a.Name}, {b.Name})",
            Items = result
        };
    }

    /// <summary>Difference (items in A but not in B)</summary>
    public UserList Trim(UserList a, UserList b)
    {
        var bPaths = b.Items.Select(i => i.FilePath).ToHashSet();
        return new UserList
        {
            Name = $"{a.Name} - {b.Name}",
            SourceQuery = $"trim({a.Name}, {b.Name})",
            Items = a.Items.Where(i => !bPaths.Contains(i.FilePath)).ToList()
        };
    }

    /// <summary>Append B's items to A (union, updates A in place)</summary>
    public UserList Append(UserList target, UserList source)
    {
        var existing = target.Items.Select(i => i.FilePath).ToHashSet();
        var toAdd = source.Items.Where(i => !existing.Contains(i.FilePath)).ToList();
        target.Items.AddRange(toAdd);
        target.ModifiedAt = DateTime.UtcNow;
        return target;
    }

    public static UserList CreateFromSearchResults(string name, string? sourceQuery, IEnumerable<SearchResultDto> items)
    {
        return new UserList
        {
            Name = name,
            SourceQuery = sourceQuery,
            Items = items.Select(r => new UserListItem
            {
                FileName = r.FileName,
                FilePath = r.FilePath ?? "",
                DataSetName = r.DataSetName,
                Date = r.Date,
                PageCount = r.PageCount,
                Distance = r.Distance,
                Names = r.Names,
                ThumbnailPath = r.ThumbnailPath
            }).ToList()
        };
    }

    private async Task PersistAsync(List<UserList> lists)
    {
        var json = JsonSerializer.Serialize(lists);
        await _js.InvokeVoidAsync("pageState.set", StorageKey, json);
    }
}

public class UserList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public string? SourceQuery { get; set; }
    public List<UserListItem> Items { get; set; } = new();
}

public class UserListItem
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? DataSetName { get; set; }
    public DateTime? Date { get; set; }
    public int PageCount { get; set; }
    public double Distance { get; set; }
    public List<string>? Names { get; set; }
    public string? ThumbnailPath { get; set; }
}
