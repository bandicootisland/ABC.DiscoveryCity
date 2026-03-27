using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ABC.DiscoveryCity.Words.Common.Models
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
    public struct SentenceSignature
    {
        // Identity & Semantic Meaning (32 bytes)
        public Guid Id;            // Primary Key from ParentDocuments.SentenceIds
        public Guid SemanticId;    // 384-dim vector quantized to 16 bytes (Product Quantization)
        
        // Document Reference (4 bytes)
        public int DocId;          // Mapping to the 660K document records
        
        // The 7 Universal Interrogatives (14 bytes - Signed Int16 for Postgres Compatibility)
        public short Who;          // Agents/Entities
        public short What;         // Actions/Concepts
        public short Where;        // Locations
        public short When;         // Time/Tense
        public short Which;        // Classification/Selection
        public short Why;          // Intent/Sentiment
        public short How;          // Modality/Manner

        // The "Safe" Padding (14 bytes)
        // This replaces the 'unsafe fixed byte' approach keeping the struct identical at exactly 64 bytes.
        private Padding14 _padding;
    }

    [InlineArray(14)]
    public struct Padding14
    {
        private byte _element;
    }
}
