// ============================================================================
// WordCity Document Envelope UUIDv8 Specification — v1
// ============================================================================
//
// Purpose: A separate, document-level identifier that wraps a sequence of
//          Sentence IDs. Carries metadata that doesn't belong in individual
//          sentences: total count, integrity boundaries, scramble state,
//          and document-wide checksums.
//
// This ID is stored ONCE per document (e.g. a column on the Document table).
// Sentence IDs remain pure and uniform — no special-casing first/last.
//
// ┌─────────────────────── UPPER 64 BITS ───────────────────────┐
// │ Bits  0–47  (48) : Timestamp — when the envelope was sealed │
// │ Bits 48–51  ( 4) : Version = 0x8 (UUIDv8)                   │
// │ Bits 52      ( 1) : Scramble Flag (1 = sentences scrambled)  │
// │ Bits 53–63  (11) : Doc Flags — reserved for future use       │
// │                     (permissions, content type, version, etc) │
// └─────────────────────────────────────────────────────────────-┘
// ┌─────────────────────── LOWER 64 BITS ───────────────────────┐
// │ Bits 64–65  ( 2) : Variant = 0b10 (standard UUID)           │
// │ Bits 66–81  (16) : Total Sentence Count (max 65,535)        │
// │ Bits 82–97  (16) : First Sequence Index — array index of    │
// │                     the first "integrity" sentence           │
// │                     (skips front matter)                     │
// │ Bits 98–113 (16) : Document Checksum — XOR-fold of ALL      │
// │                     sentence positional checksums (16 bits)  │
// │ Bits 114–127(14) : Token Hash (when scrambled) or            │
// │                     Entropy (when not scrambled)              │
// └─────────────────────────────────────────────────────────────-┘
//
// ============================================================================
// SCRAMBLE ALGORITHM
// ============================================================================
//
// Goal: Deliver sentences in a deterministic but non-obvious order so that
//       only a client with the correct token can reconstruct reading order.
//
// Method: Fisher-Yates shuffle seeded by the scramble token.
//
//   1. The scramble token is a 64-bit value (ulong) — issued to the client
//      as part of their auth/session, stored server-side per-document.
//   2. At seal time, the token seeds a deterministic PRNG.
//   3. Fisher-Yates produces a permutation of sentence indices.
//   4. Sentences (IDs + texts) are reordered by this permutation.
//   5. The token's 14-bit hash is embedded in the envelope so the client
//      can verify they have the right token before attempting unshuffle.
//
// Unshuffle: regenerate the same permutation from the token, then invert it.
//
// NOTE: Chain links will NOT validate on scrambled data. This is by design.
//       Unshuffle first, then validate chains.
//
// ============================================================================
// TOKEN HASH (14 bits)
// ============================================================================
//
//   Take the 64-bit token.
//   XOR-fold: upper 32 XOR lower 32 → 32 bits.
//   XOR-fold: upper 16 XOR lower 16 → 16 bits.
//   Take the lower 14 bits.
//
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ABC.DiscoveryCity.Words.Common.Models;
// ── Envelope Fields ──

public readonly struct DocumentEnvelopeFields
{
    public long TimestampMs { get; init; }
    public bool IsScrambled { get; init; }
    public ushort DocFlags { get; init; }
    public ushort TotalSentenceCount { get; init; }
    public ushort FirstSequenceIndex { get; init; }
    public ushort DocumentChecksum { get; init; }
    public ushort TokenHashOrEntropy { get; init; }

    public DateTimeOffset Timestamp =>
        DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] sentences={TotalSentenceCount} " +
        $"firstSeq={FirstSequenceIndex} docChk=0x{DocumentChecksum:X4} " +
        $"scrambled={IsScrambled} " +
        (IsScrambled ? $"tokenHash=0x{TokenHashOrEntropy:X4}" : $"entropy={TokenHashOrEntropy}") +
        $" flags=0x{DocFlags:X3}";
}

// ── Envelope Builder ──

public static class DocumentEnvelopeBuilder
{
    /// <summary>
    /// Seals a document WITHOUT scrambling.
    /// Sentence arrays are NOT modified — this just computes and returns the envelope ID.
    /// </summary>
    /// <param name="sentenceIds">All sentence IDs in reading order.</param>
    /// <param name="firstSequenceIndex">Index of the first "integrity" sentence (after front matter).</param>
    /// <param name="docFlags">Optional 11-bit flags for future use.</param>
    /// <param name="timestampMs">Optional explicit timestamp.</param>
    public static Guid Seal(
        Guid[] sentenceIds,
        ushort firstSequenceIndex = 0,
        ushort docFlags = 0,
        long? timestampMs = null)
    {
        long ts = timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ushort count = (ushort)Math.Min(sentenceIds.Length, 65535);
        ushort docChecksum = ComputeDocumentChecksum(sentenceIds);
        ushort entropy = GenerateEntropy14();

        return Pack(ts, isScrambled: false, docFlags, count,
                    firstSequenceIndex, docChecksum, entropy);
    }

    /// <summary>
    /// Seals a document WITH scrambling.
    /// Sentence arrays are shuffled IN-PLACE using the token as seed.
    /// Returns the envelope ID.
    /// </summary>
    /// <param name="sentenceIds">Sentence IDs — will be shuffled in-place.</param>
    /// <param name="sentenceTexts">Sentence texts — will be shuffled in-place (same permutation).</param>
    /// <param name="scrambleToken">64-bit token that seeds the shuffle.</param>
    /// <param name="firstSequenceIndex">Index of the first "integrity" sentence (before shuffle).</param>
    /// <param name="docFlags">Optional 11-bit flags.</param>
    /// <param name="timestampMs">Optional explicit timestamp.</param>
    public static Guid SealScrambled(
        Guid[] sentenceIds,
        string[] sentenceTexts,
        ulong scrambleToken,
        ushort firstSequenceIndex = 0,
        ushort docFlags = 0,
        long? timestampMs = null)
    {
        if (sentenceIds.Length != sentenceTexts.Length)
            throw new ArgumentException("ID and text arrays must be the same length.");

        long ts = timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ushort count = (ushort)Math.Min(sentenceIds.Length, 65535);

        // Compute checksum BEFORE scrambling (based on correct reading order)
        ushort docChecksum = ComputeDocumentChecksum(sentenceIds);

        // Scramble both arrays with the same permutation
        int[] permutation = GeneratePermutation(sentenceIds.Length, scrambleToken);
        ApplyPermutation(sentenceIds, permutation);
        ApplyPermutation(sentenceTexts, permutation);

        ushort tokenHash = ComputeTokenHash(scrambleToken);

        return Pack(ts, isScrambled: true, docFlags, count,
                    firstSequenceIndex, docChecksum, tokenHash);
    }

    /// <summary>
    /// Unscrambles sentence arrays in-place using the token.
    /// Call this before chain validation.
    /// Returns true if the token hash matches the envelope.
    /// </summary>
    public static bool Unshuffle(
        Guid envelopeId,
        Guid[] sentenceIds,
        string[] sentenceTexts,
        ulong scrambleToken)
    {
        var fields = DocumentEnvelopeReader.Unpack(envelopeId);

        if (!fields.IsScrambled)
            return true; // Nothing to do

        // Verify token before attempting unshuffle
        ushort expectedHash = ComputeTokenHash(scrambleToken);
        if (fields.TokenHashOrEntropy != expectedHash)
            return false; // Wrong token

        // Generate the same permutation, then invert it
        int[] permutation = GeneratePermutation(sentenceIds.Length, scrambleToken);
        int[] inverse = InvertPermutation(permutation);

        ApplyPermutation(sentenceIds, inverse);
        ApplyPermutation(sentenceTexts, inverse);

        return true;
    }

    // ── Packing ──

    public static Guid Pack(
        long timestampMs, bool isScrambled, ushort docFlags,
        ushort totalSentences, ushort firstSeqIndex,
        ushort docChecksum, ushort tokenHashOrEntropy)
    {
        // ── UPPER 64 BITS ──
        // [Timestamp: 48] | [Version: 4] | [Scramble: 1] | [DocFlags: 11]
        ulong upper = ((ulong)timestampMs & 0xFFFF_FFFF_FFFFUL) << 16;
        upper |= 0x8UL << 12;                                          // Version 8
        upper |= (isScrambled ? 1UL : 0UL) << 11;                      // Scramble flag
        upper |= (ulong)docFlags & 0x7FFUL;                            // 11 bits

        // ── LOWER 64 BITS ──
        // [Variant: 2] | [Count: 16] | [FirstSeq: 16] | [DocChk: 16] | [TokenHash/Ent: 14]
        ulong lower = 2UL << 62;                                        // Variant 10
        lower |= ((ulong)totalSentences & 0xFFFFUL) << 46;
        lower |= ((ulong)firstSeqIndex & 0xFFFFUL) << 30;
        lower |= ((ulong)docChecksum & 0xFFFFUL) << 14;
        lower |= (ulong)tokenHashOrEntropy & 0x3FFFUL;                 // 14 bits

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], upper);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], lower);
        return new Guid(bytes, bigEndian: true);
    }

    // ── Document Checksum ──

    /// <summary>
    /// XOR-folds all sentence positional checksums into 16 bits.
    /// </summary>
    public static ushort ComputeDocumentChecksum(Guid[] sentenceIds)
    {
        uint xorAccum = 0;
        for (int i = 0; i < sentenceIds.Length; i++)
        {
            var fields = SentenceIdReader.Unpack(sentenceIds[i]);
            xorAccum ^= fields.PositionalChecksum;
        }
        return (ushort)(xorAccum & 0xFFFF);
    }

    // ── Token Hash ──

    /// <summary>
    /// XOR-folds a 64-bit token into 14 bits for verification.
    /// </summary>
    public static ushort ComputeTokenHash(ulong token)
    {
        // 64 → 32
        uint fold32 = (uint)(token >> 32) ^ (uint)(token & 0xFFFFFFFF);
        // 32 → 16
        ushort fold16 = (ushort)((fold32 >> 16) ^ (fold32 & 0xFFFF));
        // Take lower 14
        return (ushort)(fold16 & 0x3FFF);
    }

    // ── Scramble: Fisher-Yates with deterministic seed ──

    /// <summary>
    /// Generates a deterministic permutation using Fisher-Yates shuffle
    /// seeded by the scramble token.
    /// </summary>
    public static int[] GeneratePermutation(int length, ulong seed)
    {
        int[] perm = new int[length];
        for (int i = 0; i < length; i++)
            perm[i] = i;

        // Simple but effective: use the seed to drive a deterministic sequence.
        // We use a xorshift64 PRNG for speed and good distribution.
        ulong state = seed;
        if (state == 0) state = 0xDEADBEEFCAFEBABE; // Avoid zero-state

        for (int i = length - 1; i > 0; i--)
        {
            // xorshift64
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;

            int j = (int)(state % (ulong)(i + 1));
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        return perm;
    }

    /// <summary>
    /// Inverts a permutation array. If perm[i] = j, then inverse[j] = i.
    /// </summary>
    public static int[] InvertPermutation(int[] permutation)
    {
        int[] inverse = new int[permutation.Length];
        for (int i = 0; i < permutation.Length; i++)
            inverse[permutation[i]] = i;
        return inverse;
    }

    /// <summary>
    /// Applies a permutation to an array. result[i] = source[perm[i]].
    /// </summary>
    private static void ApplyPermutation<T>(T[] array, int[] permutation)
    {
        T[] copy = new T[array.Length];
        Array.Copy(array, copy, array.Length);
        for (int i = 0; i < array.Length; i++)
            array[i] = copy[permutation[i]];
    }

    private static ushort GenerateEntropy14()
    {
        Span<byte> buf = stackalloc byte[2];
        RandomNumberGenerator.Fill(buf);
        return (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(buf) & 0x3FFF);
    }
}

// ── Envelope Reader ──

public static class DocumentEnvelopeReader
{
    public static DocumentEnvelopeFields Unpack(Guid envelopeId)
    {
        Span<byte> bytes = stackalloc byte[16];
        envelopeId.TryWriteBytes(bytes, bigEndian: true, out _);

        ulong upper = BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]);
        ulong lower = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);

        return new DocumentEnvelopeFields
        {
            TimestampMs = (long)((upper >> 16) & 0xFFFF_FFFF_FFFFUL),
            IsScrambled = ((upper >> 11) & 1) == 1,
            DocFlags = (ushort)(upper & 0x7FF),
            TotalSentenceCount = (ushort)((lower >> 46) & 0xFFFF),
            FirstSequenceIndex = (ushort)((lower >> 30) & 0xFFFF),
            DocumentChecksum = (ushort)((lower >> 14) & 0xFFFF),
            TokenHashOrEntropy = (ushort)(lower & 0x3FFF),
        };
    }
}

// ── Envelope Validator ──

public static class DocumentEnvelopeValidator
{
    /// <summary>
    /// Validates the envelope metadata against the actual sentence data.
    /// Sentences must be in correct reading order (unscrambled) before calling this.
    /// </summary>
    public static ValidationResult Validate(
        Guid envelopeId,
        Guid[] sentenceIds,
        string[] sentenceTexts)
    {
        var env = DocumentEnvelopeReader.Unpack(envelopeId);

        // Check sentence count
        if (env.TotalSentenceCount != sentenceIds.Length)
            return ValidationResult.Fail(
                $"Sentence count mismatch: envelope says {env.TotalSentenceCount}, " +
                $"actual is {sentenceIds.Length}.");

        // Check document checksum
        ushort actualChecksum = DocumentEnvelopeBuilder.ComputeDocumentChecksum(sentenceIds);
        if (env.DocumentChecksum != actualChecksum)
            return ValidationResult.Fail(
                $"Document checksum mismatch: envelope says 0x{env.DocumentChecksum:X4}, " +
                $"computed 0x{actualChecksum:X4}.");

        // Check first sequence index is within bounds
        if (env.FirstSequenceIndex >= sentenceIds.Length)
            return ValidationResult.Fail(
                $"First sequence index {env.FirstSequenceIndex} is out of bounds " +
                $"(total sentences: {sentenceIds.Length}).");

        return ValidationResult.Ok();
    }

    /// <summary>
    /// Full validation: envelope + every sentence + chain links
    /// (from firstSequenceIndex onward).
    /// Sentences must be unscrambled before calling this.
    /// </summary>
    public static (bool IsValid, int FirstBrokenIndex, string? Error) ValidateAll(
        Guid envelopeId,
        Guid[] sentenceIds,
        string[] sentenceTexts)
    {
        // 1. Envelope check
        var envResult = Validate(envelopeId, sentenceIds, sentenceTexts);
        if (!envResult.IsValid)
            return (false, -1, $"Envelope: {envResult.Error}");

        var env = DocumentEnvelopeReader.Unpack(envelopeId);

        // 2. Validate every sentence's structural DNA
        for (int i = 0; i < sentenceIds.Length; i++)
        {
            var result = SentenceIdValidator.ValidateSentence(sentenceIds[i], sentenceTexts[i]);
            if (!result.IsValid)
                return (false, i, $"Sentence {i}: {result.Error}");
        }

        // 3. Validate chain links from firstSequenceIndex onward
        //    (front matter sentences before this index are not chained)
        int start = env.FirstSequenceIndex;
        for (int i = start + 1; i < sentenceIds.Length; i++)
        {
            var result = SentenceIdValidator.ValidateChain(sentenceIds[i - 1], sentenceIds[i]);
            if (!result.IsValid)
                return (false, i, $"Sentence {i}: {result.Error}");
        }

        return (true, -1, null);
    }
}
