using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Ontology;
using ABC.DiscoveryCity.Words.Common.Ontology.Extensions;
using System;
using static ABC.DiscoveryCity.Words.Common.Ontology.LogicOp;
using static ABC.DiscoveryCity.Words.Words.F;
using static ABC.DiscoveryCity.Words.Words.C;
using static ABC.DiscoveryCity.Words.Words.D;
using static ABC.DiscoveryCity.Words.Words.K;
using static ABC.DiscoveryCity.Words.Words.M;
using static ABC.DiscoveryCity.Words.Words.P;
using static ABC.DiscoveryCity.Words.Words.S;
using static ABC.DiscoveryCity.Words.Words.T;

namespace ABC.DiscoveryCity.TestApp.Tests
{
    internal class OntologyTests
    {
        internal static void RunAll()
        {   
            Word pm25 = "PM2.5";
            

            // 1. Simple Fact (Defaults to Strength 255)
            var f1 = (fine + particulate + matter).est(pollutant);

            // 2. Weighted Causality (Strength 200/255)
            // "PM2.5 causes death" (High probability, but not absolute)
            var f2 = pm25.causa(death, strength: 200);

            // 3. Weak Correlation (Strength 50/255)
            // "Particulate matter correlates with kidney failure"
            var f3 = (fine + particulate).nexus(LogicOp.cor, kidney + failure, strength: 50);

            // 2. Pipe Syntax (Sentence)
            // Precedence works perfectly: (fine+particulate+matter) happens first.
            var f4 = fine + particulate + matter | est | pollutant;

            // 3. Pipe Syntax (Word)
            var f5 = pm25 | causa | death;

            // Standard Method Syntax
            var f10 = (fine + particulate + matter).est(pollutant);
            var f11 = smoking.causa(cancer);
            var f12 = drug.obsta(tumor);

            // Generic Syntax (Nexus)
            //var f13 = virus.nexus(LogicOp.situ, cell);


            // 4. Console Check
            Console.WriteLine(f1);
            // Output: 'fine particulate matter' --[IsA (255)--> 'pollutant'

            Console.WriteLine(f2);
            // Output: 'PM2.5' --[Cau (255)--> 'death'
        }
    }
}
