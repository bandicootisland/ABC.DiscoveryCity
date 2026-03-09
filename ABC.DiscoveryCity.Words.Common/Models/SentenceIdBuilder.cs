// ============================================================================
// WordCity Sentence UUIDv8 Specification — v4
// ============================================================================
//
// Purpose: A globally unique, self-validating, tamper-evident identifier for
//          Layer 1 Sentence objects. Embeds structural DNA so integrity can be
//          verified client-side without database lookups.
//
// Sentence IDs are PURE — they carry only sentence-level facts.
// Document-level concerns (count, scramble, front matter boundaries) live in
// the separate Document Envelope ID (see DocumentEnvelopeId.cs).
//
// ┌─────────────────────── UPPER 64 BITS ───────────────────────┐
// │ Bits  0–47  (48) : Timestamp (Unix ms)                      │
// │ Bits 48–51  ( 4) : Version = 0x8 (UUIDv8)                   │
// │ Bits 52–63  (12) : Chain Link — links to preceding sentence  │
// └─────────────────────────────────────────────────────────────-┘
// ┌─────────────────────── LOWER 64 BITS ───────────────────────┐
// │ Bits 64–65  ( 2) : Variant = 0b10 (standard UUID)           │
// │ Bits 66–81  (16) : Array Index — O(1) Layer 2 lookup        │
// │ Bits 82–89  ( 8) : Word Count (max 255)                     │
// │ Bits 90–101 (12) : Char Mass — string length (max 4,095)    │
// │ Bits 102–117(16) : Positional Checksum — word-order proof   │
// │ Bits 118–127(10) : Entropy — collision prevention            │
// └─────────────────────────────────────────────────────────────-┘
//
// Every sentence is identical in structure. No special-casing.
//
// ============================================================================
// ALGORITHM: Positional Checksum (16 bits)
// ============================================================================
//
//   checksum = 0
//   for each CONTENT word at index w (0-based), skipping DSL tokens:
//       for each char c in word (excluding DSL suffix):
//           checksum += (c * (w + 1))
//   checksum = checksum % 65521   (largest prime < 2^16, Adler-style)
//
//   DSL tokens match pattern .Identifier() and are transitory annotations.
//   See SentenceText.ContentLength() for detection logic.
//
// ============================================================================
// ALGORITHM: Chain Link (12 bits)
// ============================================================================
//
//   XOR-fold the previous sentence's 128-bit GUID:
//     16 bytes → XOR first/last 8 → XOR first/last 4 → take lower 12 bits.
//   First sentence in a chain: chainLink = 0.
//
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using ABC.DiscoveryCity.Words.Common;

namespace ABC.DiscoveryCity.Words.Common.Models;
/// <summary>
/// Builds a tamper-evident UUIDv8 for a WordCity Sentence.
/// Thread-safe. Zero heap allocations on the hot path.
/// </summary>
public static class SentenceIdBuilder
{
    /// <summary>
    /// Computes positional checksum, automatically excluding DSL annotations.
    /// Delegates to SentenceText.ComputePositionalChecksum().
    /// </summary>
    public static ushort ComputePositionalChecksum(ReadOnlySpan<char> sentence)
        => SentenceText.ComputePositionalChecksum(sentence);

    /// <summary>
    /// Counts content words, automatically excluding DSL-only tokens.
    /// Delegates to SentenceText.CountWords().
    /// </summary>
    public static byte CountWords(ReadOnlySpan<char> sentence)
        => SentenceText.CountWords(sentence);

    public static ushort ComputeChainLink(Guid previousSentenceId)
    {
        if (previousSentenceId == Guid.Empty) return 0;

        Span<byte> bytes = stackalloc byte[16];
        previousSentenceId.TryWriteBytes(bytes, bigEndian: true, out _);

        Span<byte> fold8 = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
            fold8[i] = (byte)(bytes[i] ^ bytes[i + 8]);

        uint fold4 = 0;
        fold4 |= (uint)(fold8[0] ^ fold8[4]) << 24;
        fold4 |= (uint)(fold8[1] ^ fold8[5]) << 16;
        fold4 |= (uint)(fold8[2] ^ fold8[6]) << 8;
        fold4 |= (uint)(fold8[3] ^ fold8[7]);

        return (ushort)(fold4 & 0xFFF);
    }

    /// <summary>
    /// Generates UUIDv8 identifiers for a collection of sentences,
    /// maintaining the integrity chain between them.
    /// </summary>
    public static List<Guid> GenerateIds(IReadOnlyList<string> sentences)
    {
        var ids = new List<Guid>(sentences.Count);
        Guid previousId = Guid.Empty;

        for (int i = 0; i < sentences.Count; i++)
        {
            Guid id = Generate(sentences[i], (ushort)i, previousId);
            ids.Add(id);
            previousId = id;
        }

        return ids;
    }

    /// <summary>
    /// Generates UUIDv8 identifiers for a collection of Sentence structs,
    /// maintaining the integrity chain between them.
    /// Uses Sentence.text for raw content (matching BookCity's approach).
    /// </summary>
    public static List<Guid> GenerateIds(IReadOnlyList<Sentence> sentences)
    {
        var ids = new List<Guid>(sentences.Count);
        Guid previousId = Guid.Empty;

        for (int i = 0; i < sentences.Count; i++)
        {
            Guid id = Generate(sentences[i].text, (ushort)i, previousId);
            ids.Add(id);
            previousId = id;
        }

        return ids;
    }

    /// <summary>
    /// Main entry point — generates a Sentence UUIDv8 from raw text.
    /// All metrics (word count, char mass, checksum) automatically exclude DSL annotations.
    /// </summary>
    public static Guid Generate(
        ReadOnlySpan<char> sentence,
        ushort arrayIndex,
        Guid previousSentenceId,
        long? timestampMs = null)
    {
        long ts = timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ushort chainLink = ComputeChainLink(previousSentenceId);
        byte wordCount = CountWords(sentence);
        ushort charMass = SentenceText.CharMass(sentence);
        ushort checksum = ComputePositionalChecksum(sentence);

        return Pack(ts, chainLink, arrayIndex, wordCount, charMass, checksum);
    }

    public static Guid Pack(
        long timestampMs, ushort chainLink, ushort arrayIndex,
        byte wordCount, ushort charMass, ushort checksum)
    {
        ulong upper = ((ulong)timestampMs & 0xFFFF_FFFF_FFFFUL) << 16;
        upper |= 0x8UL << 12;
        upper |= (ulong)chainLink & 0xFFFUL;

        ulong lower = 2UL << 62;
        lower |= ((ulong)arrayIndex & 0xFFFFUL) << 46;
        lower |= ((ulong)wordCount & 0xFFUL) << 38;
        lower |= ((ulong)charMass & 0xFFFUL) << 26;
        lower |= ((ulong)checksum & 0xFFFFUL) << 10;
        lower |= (ulong)GenerateEntropy() & 0x3FFUL;

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], upper);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], lower);
        return new Guid(bytes, bigEndian: true);
    }

    private static ushort GenerateEntropy()
    {
        Span<byte> buf = stackalloc byte[2];
        RandomNumberGenerator.Fill(buf);
        return (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(buf) & 0x3FF);
    }
}

// ── Sentence ID Fields & Reader ──

public readonly struct SentenceIdFields
{
    public long TimestampMs { get; init; }
    public ushort ChainLink { get; init; }
    public ushort ArrayIndex { get; init; }
    public byte WordCount { get; init; }
    public ushort CharMass { get; init; }
    public ushort PositionalChecksum { get; init; }
    public ushort Entropy { get; init; }

    public DateTimeOffset Timestamp =>
        DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] idx={ArrayIndex} words={WordCount} chars={CharMass} " +
        $"chk=0x{PositionalChecksum:X4} chain=0x{ChainLink:X3} ent={Entropy}";
}

public static class SentenceIdReader
{
    public static SentenceIdFields Unpack(Guid sentenceId)
    {
        Span<byte> bytes = stackalloc byte[16];
        sentenceId.TryWriteBytes(bytes, bigEndian: true, out _);

        ulong upper = BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]);
        ulong lower = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);

        return new SentenceIdFields
        {
            TimestampMs = (long)((upper >> 16) & 0xFFFF_FFFF_FFFFUL),
            ChainLink = (ushort)(upper & 0xFFF),
            ArrayIndex = (ushort)((lower >> 46) & 0xFFFF),
            WordCount = (byte)((lower >> 38) & 0xFF),
            CharMass = (ushort)((lower >> 26) & 0xFFF),
            PositionalChecksum = (ushort)((lower >> 10) & 0xFFFF),
            Entropy = (ushort)(lower & 0x3FF),
        };
    }
}

// ── Sentence Validator (pure sentence-level checks) ──

public static class SentenceIdValidator
{
    public static ValidationResult ValidateSentence(Guid sentenceId, ReadOnlySpan<char> sentenceText)
    {
        var fields = SentenceIdReader.Unpack(sentenceId);

        byte actualWords = SentenceIdBuilder.CountWords(sentenceText);
        if (actualWords != fields.WordCount)
            return ValidationResult.Fail(
                $"Word count mismatch: expected {fields.WordCount}, got {actualWords}");

        ushort actualChars = SentenceText.CharMass(sentenceText);
        if (actualChars != fields.CharMass)
            return ValidationResult.Fail(
                $"Char mass mismatch: expected {fields.CharMass}, got {actualChars}");

        ushort actualChecksum = SentenceIdBuilder.ComputePositionalChecksum(sentenceText);
        if (actualChecksum != fields.PositionalChecksum)
            return ValidationResult.Fail(
                $"Positional checksum mismatch: expected 0x{fields.PositionalChecksum:X4}, " +
                $"got 0x{actualChecksum:X4}. Words may have been transposed, injected, or modified.");

        return ValidationResult.Ok();
    }

    public static ValidationResult ValidateChain(Guid previousSentenceId, Guid currentSentenceId)
    {
        var currentFields = SentenceIdReader.Unpack(currentSentenceId);
        ushort expected = SentenceIdBuilder.ComputeChainLink(previousSentenceId);

        if (currentFields.ChainLink != expected)
            return ValidationResult.Fail(
                $"Chain link broken: expected 0x{expected:X3}, got 0x{currentFields.ChainLink:X3}. " +
                "A sentence may have been deleted, inserted, or reordered.");

        return ValidationResult.Ok();
    }
}

public readonly struct ValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }

    public static ValidationResult Ok() => new() { IsValid = true };
    public static ValidationResult Fail(string error) => new() { IsValid = false, Error = error };

    public override string ToString() => IsValid ? "Valid" : $"INVALID: {Error}";
}
