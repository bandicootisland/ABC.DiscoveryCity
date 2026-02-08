using ABC.DiscoveryCity.Words;
using ABC.DiscoveryCity.Words.Common;


namespace ABC.DiscoveryCity.Words.Common.Ontology.Extensions
{
    public static class LogicExtensions
    {
        extension(Word subject)
        {
            // --- 1. IDENTITY (EST / IDEM) ---

            /// <summary>
            /// Defines a Taxonomy: Subject IS A Target.
            /// Example: shrimp.Est(crustacean)
            /// </summary>
            public LogicToken est(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.est, target.text, WFlags.None, strength);


            /// <summary>
            /// Defines Synonymy: Subject IS THE SAME AS Target.
            /// Example: lift.Idem(elevator)
            /// </summary>
            public LogicToken idem(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.idem, target.text, WFlags.None, strength);

            // --- 2. MECHANISM (CAUSA / OBSTA) ---

            /// <summary>
            /// Defines Causality: Subject CAUSES Target.
            /// Example: smoking.Causa(cancer)
            /// </summary>
            public LogicToken causa(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.causa, target.text, WFlags.None, strength);


            /// <summary>
            /// Defines Prevention: Subject BLOCKS/INHIBITS Target.
            /// Example: drug.Obsta(tumor)
            /// </summary>
            public LogicToken obsta(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.obsta, target.text, WFlags.None, strength);

            // --- 3. STRUCTURE (PARS / SITU) ---

            /// <summary>
            /// Defines Mereology: Subject IS PART OF Target.
            /// Example: pedal.Pars(bicycle)
            /// </summary>
            public LogicToken pars(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.pars, target.text, WFlags.None, strength);

            /// <summary>
            /// Defines Topology: Subject IS LOCATED IN Target.
            /// Example: virus.Situ(cell)
            /// </summary>
            public LogicToken situ(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.situ, target.text, WFlags.None, strength);

            // --- 4. GENERIC NEXUS (ANY LOGIC) ---            
            public LogicToken nexus(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.nexus, target.text, WFlags.None, strength);
        }
        extension(Sentence subject)
        {
            // --- 1. IDENTITY (EST / IDEM) ---

            /// <summary>
            /// Defines a Taxonomy: Subject IS A Target.
            /// Example: shrimp.Est(crustacean)
            /// </summary>            

            public LogicToken est(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.est, target.text, WFlags.None, strength);

            /// <summary>
            /// Defines Synonymy: Subject IS THE SAME AS Target.
            /// Example: lift.Idem(elevator)
            /// </summary>
            public LogicToken idem(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.idem, target.text, WFlags.None, strength);

            // --- 2. MECHANISM (CAUSA / OBSTA) ---

            /// <summary>
            /// Defines Causality: Subject CAUSES Target.
            /// Example: smoking.Causa(cancer)
            /// </summary>
            

            public LogicToken causa(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.causa, target.text, WFlags.None, strength);

            /// <summary>
            /// Defines Prevention: Subject BLOCKS/INHIBITS Target.
            /// Example: drug.Obsta(tumor)
            /// </summary>

            public  LogicToken obsta(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.obsta, target.text, WFlags.None, strength);

            // --- 3. STRUCTURE (PARS / SITU) ---

            /// <summary>
            /// Defines Mereology: Subject IS PART OF Target.
            /// Example: pedal.Pars(bicycle)
            /// </summary>
            public LogicToken pars(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.pars, target.text, WFlags.None, strength);

            /// <summary>
            /// Defines Topology: Subject IS LOCATED IN Target.
            /// Example: virus.Situ(cell)
            /// </summary>
            public LogicToken situ(Word target, byte strength = 255)
                => new LogicToken(subject.text, LogicOp.situ, target.text, WFlags.None, strength);

            // --- 4. GENERIC NEXUS (ANY LOGIC) ---
            // Formerly "Rel" -> Now "Nexus" (Connection/Bond)

            public LogicToken nexus(LogicOp op, Word target, byte strength = 255)
                => new LogicToken(subject.text, op, target.text, WFlags.None, strength);

            
            public LogicToken nexus(LogicOp op, Sentence target, byte strength = 255)
                    => new LogicToken(subject.text, op, target.text, WFlags.None, strength);

            
        }
    }
    
}
