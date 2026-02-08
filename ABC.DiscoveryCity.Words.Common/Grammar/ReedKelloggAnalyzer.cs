using ABC.DiscoveryCity.Words.Common.Grammar;

public static class ReedKelloggAnalyzer
{
    public static SentenceSpine ExtractSpine(List<Word> sentence)
    {
        var spine = new SentenceSpine();

        // ---------------------------------------------------------
        // STEP 1: FIND THE MAIN VERB (The "Time Test")
        // Logic: Scan for the first 'Finite Verb'. 
        // In a real NLP engine, we'd check for auxiliary verbs (has, had, was).
        // Here, we take the first verb that isn't a participle (ending in 'ing' without a helper).
        // ---------------------------------------------------------

        var verbIndex = -1;

        for (int i = 0; i < sentence.Count; i++)
        {
            if (sentence[i].Type == PartOfSpeech.Verb)
            {
                // Simple heuristic: If it's the first verb we see, grab it.
                spine.MainVerb = sentence[i];
                verbIndex = i;
                break; // Stop! We found the anchor.
            }
        }

        // If no verb found, it's a fragment (like "Rings, chains.")
        if (verbIndex == -1) return spine;

        // ---------------------------------------------------------
        // STEP 2: FIND THE SUBJECT (The "Who?" Test)
        // Logic: Look BACKWARDS from the verb (Index 0 to verbIndex - 1).
        // We want the *Head Noun*. Usually the last noun before the verb.
        // ---------------------------------------------------------

        for (int i = verbIndex - 1; i >= 0; i--)
        {
            var w = sentence[i];

            // Skip adjectives, adverbs, articles
            if (w.Type == PartOfSpeech.Noun || w.Type == PartOfSpeech.Pronoun)
            {
                spine.Subject = w;
                break; // Stop! We found the nearest actor.
            }
        }

        // ---------------------------------------------------------
        // STEP 3: FIND THE OBJECT (The "What?" Test)
        // Logic: Look FORWARDS from the verb (verbIndex + 1 to End).
        // We want the first noun that isn't trapped in a prepositional phrase.
        // ---------------------------------------------------------

        bool inPrepositionalPhrase = false;

        for (int i = verbIndex + 1; i < sentence.Count; i++)
        {
            var w = sentence[i];

            if (w.Type == PartOfSpeech.Preposition)
            {
                inPrepositionalPhrase = true;
                continue;
            }

            // If we hit a noun and we aren't "in a trap" (prep phrase), it's the Object.
            if (!inPrepositionalPhrase && (w.Type == PartOfSpeech.Noun || w.Type == PartOfSpeech.Pronoun))
            {
                spine.DirectObject = w;
                break; // Stop! Found the receiver.
            }

            // Note: A real parser would have logic to "exit" the prepositional phrase,
            // but for this heuristic, we assume the first noun after a prep is the object of the prep, not the verb.
        }

        return spine;
    }
}