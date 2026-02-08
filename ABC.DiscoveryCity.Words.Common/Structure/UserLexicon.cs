using ABC.DiscoveryCity.Words.Common;

namespace ABC.DiscoveryCity.Words.Structure
{
    public static class UserLexicon
    {
        // Dynamic Storage (Thread-safe concurrent dictionaries would be ideal for server, 
        // but for Blazor WASM standard Dict is fine if single-threaded)

        // Map: "MyWord" -> -1
        private static Dictionary<string, int> _userVocab = new(StringComparer.OrdinalIgnoreCase);
        // Map: -1 -> "MyWord"
        private static Dictionary<int, string> _userText = new();
        // Map: -1 -> Definition Sentence
        private static Dictionary<int, Sentence> _userDefinitions = new();

        private static int _nextId = -1;

        public static int Add(string headword, Sentence definition)
        {
            if (_userVocab.TryGetValue(headword, out int existingId))
            {
                // Update definition? Or ignore? Let's update.
                _userDefinitions[existingId] = definition;
                return existingId;
            }

            int id = _nextId--;
            _userVocab[headword] = id;
            _userText[id] = headword;
            _userDefinitions[id] = definition;
            return id;
        }

        public static bool TryLookup(ReadOnlySpan<char> text, out int id)
        {
            // Note: CollectionsMarshal.GetValueRef... doesn't work easily with Span on standard Dictionary yet
            // unless using .NET 9 AlternateLookup.
            // For now, we ToString() or use AlternateLookup if available.
            return _userVocab.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out id);
        }

        public static string GetText(int id) => _userText.TryGetValue(id, out var s) ? s : "";

        public static Sentence GetDefinition(int id) => _userDefinitions.TryGetValue(id, out var s) ? s : new Sentence(Word.None);
    }
}