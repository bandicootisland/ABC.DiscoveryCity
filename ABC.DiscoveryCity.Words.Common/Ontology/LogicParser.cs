using System;

namespace ABC.DiscoveryCity.Words.Common.Ontology
{
    public static class LogicParser
    {
        public static LogicOp ParseLogic(string tag)
        {
            return tag.ToLowerInvariant() switch
            {
                nameof(LogicOp.est) => LogicOp.est,
                nameof(LogicOp.idem) => LogicOp.idem,
                nameof(LogicOp.contra) => LogicOp.contra,
                nameof(LogicOp.pars) => LogicOp.pars,
                nameof(LogicOp.situ) => LogicOp.situ,
                nameof(LogicOp.totum) => LogicOp.totum,
                nameof(LogicOp.causa) => LogicOp.causa,
                nameof(LogicOp.dum) => LogicOp.dum,                
                nameof(LogicOp.diversum) => LogicOp.diversum,
                nameof(LogicOp.obsta) => LogicOp.obsta,
                nameof(LogicOp.ortus) => LogicOp.ortus,
                nameof(LogicOp.usus) => LogicOp.usus,
                _ => LogicOp.none
            };
        }

        public static WFlags ParseFlag(string tag)
        {
            return tag.ToLowerInvariant() switch
            {
                "u" => WFlags.UpperFirst,
                "ua" => WFlags.UpperAll,
                "ns" => WFlags.NoSpace,
                "qo" => WFlags.QuoteOpen,
                "qc" => WFlags.QuoteClose,
                "r" => WFlags.IsRaw,
                _ => WFlags.None
            };
        }
    }
}