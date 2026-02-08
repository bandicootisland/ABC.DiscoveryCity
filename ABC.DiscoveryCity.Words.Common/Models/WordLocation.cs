using System.Runtime.InteropServices;

namespace ABC.DiscoveryCity.Words.Common.Models
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WordLocation
    {
        public int Ordinal;     // 4 bytes
        public int PageIndex;   // 4 bytes
        public float X;         // 4 bytes
        public float Y;         // 4 bytes
        // Total: 16 bytes per word. 
    }
}
