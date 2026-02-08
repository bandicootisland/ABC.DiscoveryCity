using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

public static class StringCache
{
    // --- 1. Static Singletons for High-Frequency Tokens ---
    // These are pre-allocated once for the entire lifecycle of the app.
    public static readonly string TagBoldStart = "<b>";
    public static readonly string TagBoldEnd = "</b>";
    public static readonly string TagItalicStart = "<i>";
    public static readonly string TagItalicEnd = "</i>";
    public static readonly string TagParaStart = "<p>";
    public static readonly string TagParaEnd = "</p>";
    public static readonly string TagBreak = "<br>";
    public static readonly string TagBreakClosed = "<br/>";
    public static readonly string TagDivEnd = "</div>";

    public static readonly string Space = " ";
    public static readonly string Comma = ",";
    public static readonly string Period = ".";
    public static readonly string Colon = ":";
    public static readonly string Semicolon = ";";
    public static readonly string Empty = "";

    // --- Common Dictionary Abbreviations ---
    // These are pre-interned for fast Word matching across dictionaries
    public static readonly string Abbr_Noun = "n.";
    public static readonly string Abbr_Verb = "v.";
    public static readonly string Abbr_Adjective = "adj.";
    public static readonly string Abbr_Adverb = "adv.";
    public static readonly string Abbr_Preposition = "prep.";
    public static readonly string Abbr_Conjunction = "conj.";
    public static readonly string Abbr_Pronoun = "pron.";
    public static readonly string Abbr_Interjection = "interj.";
    public static readonly string Abbr_Plural = "pl.";
    public static readonly string Abbr_Singular = "sing.";
    public static readonly string Abbr_Transitive = "trans.";
    public static readonly string Abbr_Intransitive = "intr.";
    public static readonly string Abbr_Obsolete = "obs.";
    public static readonly string Abbr_Archaic = "arch.";
    public static readonly string Abbr_Colloquial = "colloq.";
    public static readonly string Abbr_Figurative = "fig.";
    public static readonly string Abbr_Literal = "lit.";
    public static readonly string Abbr_Etymology = "etym.";
    public static readonly string Abbr_Compare = "cf.";
    public static readonly string Abbr_Latin = "L.";
    public static readonly string Abbr_Greek = "Gr.";
    public static readonly string Abbr_French = "Fr.";
    public static readonly string Abbr_German = "Ger.";

    // --- 2. The String Pool (Thread-Safe) ---
    // Maps "Text" -> "Text". ConcurrentDictionary for parallel dictionary loading.
    private static readonly ConcurrentDictionary<string, string> _pool
        = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    // .NET 9+ AlternateLookup for zero-allocation span-based lookups (thread-safe with ConcurrentDictionary)
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _spanLookup
        = _pool.GetAlternateLookup<ReadOnlySpan<char>>();

    // Static constructor to pre-intern all abbreviations
    static StringCache()
    {
        // Pre-intern abbreviations so they're in the pool for fast lookup
        _pool[Abbr_Noun] = Abbr_Noun;
        _pool[Abbr_Verb] = Abbr_Verb;
        _pool[Abbr_Adjective] = Abbr_Adjective;
        _pool[Abbr_Adverb] = Abbr_Adverb;
        _pool[Abbr_Preposition] = Abbr_Preposition;
        _pool[Abbr_Conjunction] = Abbr_Conjunction;
        _pool[Abbr_Pronoun] = Abbr_Pronoun;
        _pool[Abbr_Interjection] = Abbr_Interjection;
        _pool[Abbr_Plural] = Abbr_Plural;
        _pool[Abbr_Singular] = Abbr_Singular;
        _pool[Abbr_Transitive] = Abbr_Transitive;
        _pool[Abbr_Intransitive] = Abbr_Intransitive;
        _pool[Abbr_Obsolete] = Abbr_Obsolete;
        _pool[Abbr_Archaic] = Abbr_Archaic;
        _pool[Abbr_Colloquial] = Abbr_Colloquial;
        _pool[Abbr_Figurative] = Abbr_Figurative;
        _pool[Abbr_Literal] = Abbr_Literal;
        _pool[Abbr_Etymology] = Abbr_Etymology;
        _pool[Abbr_Compare] = Abbr_Compare;
        _pool[Abbr_Latin] = Abbr_Latin;
        _pool[Abbr_Greek] = Abbr_Greek;
        _pool[Abbr_French] = Abbr_French;
        _pool[Abbr_German] = Abbr_German;
    }

    /// <summary>
    /// Register custom abbreviations for a specific dictionary.
    /// Call before loading to ensure they're interned.
    /// </summary>
    public static void RegisterAbbreviations(params string[] abbreviations)
    {
        foreach (var abbr in abbreviations)
        {
            _pool.TryAdd(abbr, abbr);
        }
    }

    /// <summary>
    /// Returns a cached reference for a single character (Punctuation/Symbols).
    /// </summary>
    public static string GetChar(char c)
    {
        switch (c)
        {
            case ' ': return Space;
            case ',': return Comma;
            case '.': return Period;
            case ':': return Colon;
            case ';': return Semicolon;            
            case '(': return Intern("(");
            case ')': return Intern(")");
            case '}': return Intern("}");
            case '{': return Intern("{");
            case '[': return Intern("[");
            case ']': return Intern("]");
            default: return Intern(c.ToString());
        }
    }

    /// <summary>
    /// The Workhorse: Takes a Span (slice), checks if we know it, 
    /// and returns a string without allocation if possible.
    /// </summary>
    public static string Intern(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return Empty;

        // --- OPTIMIZATION: Manual Tag Detection ---
        // This avoids calling .ToString() for the most common HTML tags.
        // We check Length first (fastest check), then specific chars.

        int len = span.Length;
        char first = span[0];

        if (first == '<')
        {
            if (len == 3) // <b>, <i>, <p>
            {
                if (span[2] == '>')
                {
                    char mid = span[1];
                    if (mid == 'b') return TagBoldStart;
                    if (mid == 'i') return TagItalicStart;
                    if (mid == 'p') return TagParaStart;
                }
            }
            else if (len == 4) // </b>, </i>, </p>, <br>
            {
                if (span[3] == '>')
                {
                    // Check for closing tags </.>
                    if (span[1] == '/')
                    {
                        char mid = span[2];
                        if (mid == 'b') return TagBoldEnd;
                        if (mid == 'i') return TagItalicEnd;
                        if (mid == 'p') return TagParaEnd;
                    }
                    // Check for break <br>
                    else if (span[1] == 'b' && span[2] == 'r')
                    {
                        return TagBreak;
                    }
                }
            }
            else if (len == 5) // <br/>
            {
                if (span.SequenceEqual(TagBreakClosed.AsSpan())) return TagBreakClosed;
            }
            else if (len == 6) // </div>
            {
                if (span.SequenceEqual(TagDivEnd.AsSpan())) return TagDivEnd;
            }
        }

        // --- FALLBACK ---
        // If we get here, it's a word or a complex tag (e.g. <font color="...">).
        // .NET 9+ AlternateLookup: Zero-allocation lookup for existing words!

        if (_spanLookup.TryGetValue(span, out string? existing))
        {
            return existing;  // Found in pool - no allocation!
        }

        // Only allocate if truly new word - GetOrAdd is thread-safe
        string key = span.ToString();
        return _pool.GetOrAdd(key, key);
    }

    /// <summary>
    /// Stores the string in the pool if new, or returns the existing reference.
    /// Thread-safe for parallel loading.
    /// </summary>
    public static string Intern(string text)
    {
        if (string.IsNullOrEmpty(text)) return Empty;

        // GetOrAdd is thread-safe - returns existing or adds new
        return _pool.GetOrAdd(text, text);
    }

    // Optional: Call this between books/files if you want to free memory
    public static void Clear()
    {
        _pool.Clear();
    }
}