using ABC.DiscoveryCity.Words.Common;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{   
   
        public readonly struct Chars
        {
            /// <summary>
            /// Represents a period / full stop (.).
            /// </summary>
            public static readonly Chars _p = new(".");   // period,full stop
            public static readonly Chars _c = new(",");   // comma        
            public static readonly Chars _sc = new(";");  // semicolon
            public static readonly Chars _col = new(":");  // colon
            public static readonly Chars _q = new("?");   // question
            public static readonly Chars _ex = new("!");   // exclamation        
            public static readonly Chars _qi = new("\"");  // initial quote
            public static readonly Chars _qf = new("\"");  // final quote
            public static readonly Chars _pc = new("%");  // percent
            public static readonly Chars _amp = new("&");  // ampersand
            public static readonly Chars _ast = new("*");  // asterisk
            public static readonly Chars _at = new("@");  // at
            public static readonly Chars _vert = new("|");  // vertical bar
            public static readonly Chars _fwd = new("/");  // forward slash
            public static readonly Chars _bck = new("\\");  // backward slash
            public static readonly Chars _crt = new("^");  // caret

            public string Value { get; init; }
            private Chars(string value) => Value = value;

            // Catch-all for any character or string
            public static Chars _(string value) => new Chars(value);
            public static Chars _(char value) => new Chars(value.ToString());
        
    }
    
}



