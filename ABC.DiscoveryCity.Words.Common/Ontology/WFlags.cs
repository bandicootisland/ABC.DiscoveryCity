using System;

namespace ABC.DiscoveryCity.Words.Common.Ontology
{
    [Flags]
    public enum WFlags : byte
    {
        None = 0, UpperFirst = 1, UpperAll = 2, NoSpace = 4,
        IsTag = 8, IsContent = 16, QuoteOpen = 32, QuoteClose = 64, IsRaw = 128
    }
}