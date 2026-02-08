//using ABC.DiscoveryCity.Words.Common;
//using System.Collections.Frozen;
//using System.Collections.Immutable;
//using System.Runtime.InteropServices;
//using System.Text;
//using System.Collections.Generic;
//namespace ABC.DiscoveryCity.Words.Common
//{

    
//    public static class TagSet
//    {
//        public const string Grammar = "G";       // Short keys save memory!
//        public const string Definition = "D";
//        public const string Sentiment = "S";
//    }

//    public static class WordTags
//    {
//        // The "Columns"
//        // Key: Category Name (e.g., "Grammar", "Definition")
//        // Value: The Store (Ordinal -> TagValue)
//        private static readonly Dictionary<string, Dictionary<int, string>> _columns
//            = new Dictionary<string, Dictionary<int, string>>();

//        // 1. ADD TAG
//        public static void Add(int ordinal, string category, string value)
//        {
//            if (ordinal <= 0) return;

//            // Get or Create the Column for this category
//            if (!_columns.TryGetValue(category, out var store))
//            {
//                store = new Dictionary<int, string>();
//                _columns[category] = store;
//            }

//            // Add the value for this specific word
//            // (Using Indexer [] updates existing value if present)
//            store[ordinal] = value;
//        }

//        // 2. GET TAG
//        public static string? Get(int ordinal, string category)
//        {
//            // Fail fast if category doesn't exist
//            if (_columns.TryGetValue(category, out var store))
//            {
//                // Try get value for this word
//                if (store.TryGetValue(ordinal, out var val))
//                {
//                    return val;
//                }
//            }
//            return null;
//        }

//        // 3. CHECK TAG (Boolean flag check)
//        public static bool Has(int ordinal, string category)
//        {
//            if (_columns.TryGetValue(category, out var store))
//            {
//                return store.ContainsKey(ordinal);
//            }
//            return false;
//        }
//    }
//}

