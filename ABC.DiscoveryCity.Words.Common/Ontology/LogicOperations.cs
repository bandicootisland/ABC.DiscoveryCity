namespace ABC.DiscoveryCity.Words.Common.Ontology
{
    /// <summary>
    /// The Latin Semantic Dictionary.
    /// Renamed from English abbreviations to Classical Latin for scientific precision.
    /// </summary>
        public enum LogicOp : byte
        {
            none = 0,

            // --- 01-19: IDENTITAS (Identity) ---
            est = 1,   // Is A (Type of / Hypernym)
            idem = 2,   // Same As (Synonym)
            contra = 3,   // Against (Opposite)
            diversum = 4,   // Different From
            nexus = 5,   // Connected To / Related (Generic Link)

            // --- 20-39: STRUCTURA (Structure) ---
            pars = 20,  // Part Of (Mereology)
            totum  = 21,  // The Whole (Composite)
            situ = 22,  // Situated In (Location / Topology)

            // --- 40-59: MECHANISMUS (Causality) ---
            causa = 40,  // Causes (Driver)
            ortus = 41,  // Arisen From (Effect / Origin)
            cor = 42,  // Correlates With (Statistical)
            obsta = 43,  // Blocks / Inhibits (Prevention)

            // --- 60-79: TEMPUS (Time) ---
            post = 60,  // After (Sequence)
            dum = 61,  // While (Simultaneous)

            // --- 80-99: FUNCTIO (Utility) ---
            usus = 80,  // Used For
            per = 81,  // By Means Of (Agent)

            // --- 100-119: CHEMIA (Chemistry) ---
            ligat = 100, // Binds To (Covalent / Strong)
            affinis = 101, // Affinity For (Weak / Hydrogen)
            donat = 102, // Donates (Electron / Ionic)
            solvit = 103, // Dissolves / Breaks

            // --- 120-139: GENETICA (Genetics) ---
            genoma = 120, // Genome Of (Species)
            locus = 121, // Locus Of (Map Position)
            mutatio = 122, // Mutates To (Variant)
            codex = 123  // Encodes (Pointer to Raw Sequence Span)
        }
    }

