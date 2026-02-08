using ABC.DiscoveryCity.Words.Common;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{

    public static class MarkLexer
    {
        public static List<Mark> Parse(string input)
        {
            var marks = new List<Mark>();
            var buffer = new StringBuilder();
            int startIndex = 0;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (char.IsWhiteSpace(c))
                {
                    // 1. Commit the Text Mark (if any)
                    CommitBuffer(marks, buffer, MarkType.Text, ref startIndex);

                    // 2. Add the Whitespace Mark
                    marks.Add(new Mark(c.ToString(), MarkType.Whitespace, i));
                    startIndex = i + 1;
                }
                else if (char.IsPunctuation(c))
                {
                    // 1. Commit the Text Mark (if any)
                    CommitBuffer(marks, buffer, MarkType.Text, ref startIndex);

                    // 2. Add the Punctuation Mark
                    marks.Add(new Mark(c.ToString(), MarkType.Punctuation, i));
                    startIndex = i + 1;
                }
                else
                {
                    // Build the Text Mark
                    buffer.Append(c);
                }
            }

            // Commit any remaining text at the end of the string
            CommitBuffer(marks, buffer, MarkType.Text, ref startIndex);

            return marks;
        }

        private static void CommitBuffer(List<Mark> marks, StringBuilder buffer, MarkType type, ref int startIndex)
        {
            if (buffer.Length > 0)
            {
                // The Mark starts where the buffer started, not where we are now
                int actualStart = startIndex;

                marks.Add(new Mark(buffer.ToString(), type, actualStart));

                // Advance start index by the length of what we just created
                startIndex += buffer.Length;

                buffer.Clear();
            }
        }
    }

}



