using ABC.DiscoveryCity.Words.Structure;

namespace ABC.DiscoveryCity.Words.Common.Structure
{
    public readonly record struct Concept(int Id)
    {
        public bool IsValid => Id >= 0;
        public string Text => WordWeb.GetText(Id);

        // Navigation
        public ReadOnlySpan<Sense> Senses => WordWeb.GetSenses(Id);
        public ReadOnlySpan<Concept> Synonyms => WordWeb.GetSynonyms(Id); // Global/Headword synonyms

        public override string ToString() => Text;
    }

    public readonly record struct Sense(int Id)
    {
        public bool IsValid => Id >= 0;

        // 1. The Definition (Transplanted Sentence)
        public Sentence Definition => WordWeb.GetDefinitionSentence(Id);

        // 2. Link Back
        public Concept DefinedConcept => WordWeb.GetConceptForSense(Id);

        // 3. Sense-Specific Synonyms (From DictionaryDefinition.Synonyms)
        public ReadOnlySpan<Concept> Synonyms => WordWeb.GetSenseSynonyms(Id);

        // 4. Grammar / Part Of Speech
        public string PartOfSpeech => WordWeb.GetSensePOS(Id);

        public override string ToString() => IsValid ? Definition.ToString() : "<Invalid>";
    }
}