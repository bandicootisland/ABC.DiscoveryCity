namespace ABC.WordCity.Words.Common.Ontology
{
    public enum SourceType : byte
    {
        Unknown = 0,

        // Physical/Academic
        AcademicPaper = 1,  // Linked via DOI
        Book = 2,           // Linked via ISBN

        // Digital/Web
        WebResource = 10,   // General URL
        Wiki = 11,          // Specifically Wikipedia/Wikidata

        // Assets
        LocalFile = 20,     // User uploaded PDF/Text
        SystemBlob = 21     // Internal system file (config/axioms)
    }
    public static class Veracity
    {
        public const byte None = 0;
        public const byte Low = 50;         // Unverified Web / Random User
        public const byte Medium = 100;     // Pre-prints / News / Standard User
        public const byte High = 200;       // Peer Reviewed / Wikipedia / Admin
        public const byte Absolute = 255;   // System Axioms / Dictionary Definitions
    }
    public class Source
    {
        public int Id { get; set; }              // PK
        public SourceType Type { get; set; }     // e.g. LocalFile
        public byte Veracity { get; set; }       // e.g. 200 (High)

        public string Title { get; set; }        // "Analysis of PM2.5"
        public string Identifier { get; set; }   // DOI, URL, or FilePath
        public string Author { get; set; }       // "Smith et al." or "System Admin"

        // Helper to check if this source is trustworthy
        public bool IsTrusted => Veracity >= ABC.WordCity.Words.Common.Ontology.Veracity.High;
    }



}
