using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{
    using System;
    using System.Collections.Generic;

    public static class WordTextParser
    {
        extension(ReadOnlySpan<char> span)
        {
            public void Tokenize(List<Word> buffer,ref int ordinal,SentenceData? context = null)
            {
                int n = span.Length;
                int i = 0;
                bool pendingSpace = false;

                // Cache the space memory once (zero allocation reuse)
                var spaceMemory = StringCache.Space.AsMemory();

                while (i < n)
                {
                    char c = span[i];

                    // --- Case 1: HTML Tag <...> ---
                    if (c == '<')
                    {
                        int start = i;
                        while (i < n && span[i] != '>') i++;
                        int len = (i - start) + 1;

                        if (i < n)
                        {
                            // Flush pending space before tag
                            if (pendingSpace)
                            {
                                buffer.Add(new Word(spaceMemory, context, buffer.Count, ordinal++));
                                pendingSpace = false;
                            }

                            // Intern the tag string (reuses common tags like <b>, </p>)
                            var tagStr = StringCache.Intern(span.Slice(start, len));
                            buffer.Add(new Word(tagStr.AsMemory(), WFlags.IsTag, '\0', '\0', '\0', context, buffer.Count, ordinal++));
                        }
                        i++;
                        continue;
                    }

                    // --- Case 2: Punctuation ---
                    if (char.IsPunctuation(c) || char.IsSymbol(c))
                    {
                        // USER RULE: "Always want all space" -> Flush it, don't consume it.
                        if (pendingSpace)
                        {
                            buffer.Add(new Word(spaceMemory, context, buffer.Count, ordinal++));
                            pendingSpace = false;
                        }

                        // Use cached punctuation strings
                        var pStr = StringCache.GetChar(c);
                        buffer.Add(new Word(pStr.AsMemory(), context, buffer.Count, ordinal++));
                        i++;
                        continue;
                    }

                    // --- Case 3: Whitespace ---
                    if (char.IsWhiteSpace(c))
                    {
                        pendingSpace = true;
                        i++;
                        continue;
                    }

                    // --- Case 4: Content Word ---
                    if (pendingSpace)
                    {
                        buffer.Add(new Word(spaceMemory, context, buffer.Count, ordinal++));
                        pendingSpace = false;
                    }

                    int wordStart = i;
                    while (i < n)
                    {
                        char next = span[i];
                        if (char.IsWhiteSpace(next) || next == '<' || char.IsPunctuation(next) || char.IsSymbol(next))
                        {
                            break;
                        }
                        i++;
                    }

                    // Intern content words (deduplicates repeated words)
                    var wordStr = StringCache.Intern(span.Slice(wordStart, i - wordStart));
                    buffer.Add(new Word(wordStr.AsMemory(), context, buffer.Count, ordinal++));
                }

                // Final Flush
                if (pendingSpace)
                {
                    buffer.Add(new Word(spaceMemory, context, buffer.Count, ordinal++));
                }
            }
        }
    }
}
