namespace ABC.DiscoveryCity.Services;

public class AdvancedFindCriteria
{
    public string? TextQuery { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public List<string> DataSets { get; set; } = new();
    public List<string> Names { get; set; } = new();
    public bool IncludePdf { get; set; } = true;
    public bool IncludeSpreadsheet { get; set; } = true;
    public bool IncludeVideo { get; set; } = true;
    public bool IncludeAudio { get; set; } = true;
    public bool IncludeOther { get; set; } = true;
    public int? MinPages { get; set; }
    public int? MaxPages { get; set; }

    public void Reset()
    {
        TextQuery = null;
        DateFrom = null;
        DateTo = null;
        DataSets = new();
        Names = new();
        IncludePdf = true;
        IncludeSpreadsheet = true;
        IncludeVideo = true;
        IncludeAudio = true;
        IncludeOther = true;
        MinPages = null;
        MaxPages = null;
    }
}
