using System.Collections.Generic;
using Verse;

namespace ClaudeStoryteller
{
    /// <summary>
    /// One fired (or attempted) beat within an arc. Phase 1 populates Type/RequestedType/
    /// Outcome/Day/Colonists/Wealth/FoodDays/DeathsTotal/DownedTotal/Faction; Link/CircleStep/
    /// LinkReason/Expect are Phase 2 fields left null here.
    /// </summary>
    public class ArcBeatRecord : IExposable
    {
        public string Type;            // resolved defName that actually fired
        public string RequestedType;   // what Claude asked for (may differ if fallback)
        public string Outcome;         // "fired" | "failed" | "fizzled" | "blocked_disease_cooldown"
        public string Link;            // "and" | "but" | "therefore" | null (Phase 2)
        public string CircleStep;      // null in Phase 1
        public string LinkReason;      // null in Phase 1
        public string Expect;          // null in Phase 1
        public int Day;
        public int Colonists;
        public float Wealth;
        public int FoodDays;
        public int DeathsTotal;
        public int DownedTotal;
        public string Faction;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Type, "type");
            Scribe_Values.Look(ref RequestedType, "requestedType");
            Scribe_Values.Look(ref Outcome, "outcome");
            Scribe_Values.Look(ref Link, "link");
            Scribe_Values.Look(ref CircleStep, "circleStep");
            Scribe_Values.Look(ref LinkReason, "linkReason");
            Scribe_Values.Look(ref Expect, "expect");
            Scribe_Values.Look(ref Day, "day", 0);
            Scribe_Values.Look(ref Colonists, "colonists", 0);
            Scribe_Values.Look(ref Wealth, "wealth", 0f);
            Scribe_Values.Look(ref FoodDays, "foodDays", 0);
            Scribe_Values.Look(ref DeathsTotal, "deathsTotal", 0);
            Scribe_Values.Look(ref DownedTotal, "downedTotal", 0);
            Scribe_Values.Look(ref Faction, "faction");
        }
    }

    public class ArcLogEntry : IExposable
    {
        public string ArcName;
        public List<string> Events = new List<string>();
        public List<string> EventOutcomes = new List<string>(); // "fired", "skipped", "fallback"
        public string Outcome; // "completed", "interrupted", "timed_out", "stalled", "abandoned", "legacy_reset"
        public int StartDay;
        public int EndDay;
        public int ColonistCount;
        public float Wealth;
        public string Phase;
        public string NarrativeState;

        // Enriched fields (Phase 1)
        public string StoryQuestion;
        public string Summary;
        public List<string> Links = new List<string>();
        public List<string> Steps = new List<string>();
        public int Deaths;
        public int ColonistDelta;
        public float WealthDelta;
        public string Faction;
        public List<string> UnresolvedThreads = new List<string>();

        // Phase 2: resolved defNames of beats whose CircleStep was "take", for cross-arc
        // repeated-mechanism detection in ArcSummarizer.
        public List<string> TakeEventTypes = new List<string>();

        public ArcLogEntry() { }

        public ArcLogEntry(string arcName, int startDay)
        {
            ArcName = arcName;
            StartDay = startDay;
            Events = new List<string>();
            EventOutcomes = new List<string>();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref ArcName, "arcName", "");
            Scribe_Collections.Look(ref Events, "events", LookMode.Value);
            Scribe_Collections.Look(ref EventOutcomes, "eventOutcomes", LookMode.Value);
            Scribe_Values.Look(ref Outcome, "outcome", "unknown");
            Scribe_Values.Look(ref StartDay, "startDay", 0);
            Scribe_Values.Look(ref EndDay, "endDay", 0);
            Scribe_Values.Look(ref ColonistCount, "colonistCount", 0);
            Scribe_Values.Look(ref Wealth, "wealth", 0f);
            Scribe_Values.Look(ref Phase, "phase", "");
            Scribe_Values.Look(ref NarrativeState, "narrativeState", "");
            Scribe_Values.Look(ref StoryQuestion, "storyQuestion");
            Scribe_Values.Look(ref Summary, "summary");
            Scribe_Collections.Look(ref Links, "links", LookMode.Value);
            Scribe_Collections.Look(ref Steps, "steps", LookMode.Value);
            Scribe_Values.Look(ref Deaths, "deaths", 0);
            Scribe_Values.Look(ref ColonistDelta, "colonistDelta", 0);
            Scribe_Values.Look(ref WealthDelta, "wealthDelta", 0f);
            Scribe_Values.Look(ref Faction, "faction");
            Scribe_Collections.Look(ref UnresolvedThreads, "unresolvedThreads", LookMode.Value);
            Scribe_Collections.Look(ref TakeEventTypes, "takeEventTypes", LookMode.Value);

            // Handle null lists after load
            if (Events == null) Events = new List<string>();
            if (EventOutcomes == null) EventOutcomes = new List<string>();
            if (Links == null) Links = new List<string>();
            if (Steps == null) Steps = new List<string>();
            if (UnresolvedThreads == null) UnresolvedThreads = new List<string>();
            if (TakeEventTypes == null) TakeEventTypes = new List<string>();
        }

        /// <summary>
        /// Returns the event sequence as a readable pattern like "TraderCaravanArrival → Disease_Plague → RaidEnemy"
        /// </summary>
        public string GetPattern()
        {
            if (Events == null || Events.Count == 0) return "empty";
            return string.Join(" → ", Events);
        }

        /// <summary>
        /// Returns just the first event (opener) of this arc
        /// </summary>
        public string GetOpener()
        {
            if (Events == null || Events.Count == 0) return null;
            return Events[0];
        }
    }

    /// <summary>
    /// One faction's running relationship with the storyteller, built from ArcBeatRecord.Faction
    /// and death counts across arcs. Memory only — never fed into player-facing text directly,
    /// only into arc_history.recurring_factions so Claude can decide "the same tribe comes back".
    /// </summary>
    public class FactionLedgerEntry : IExposable
    {
        public string FactionName;
        public int RaidsSent;
        public int LostPawns;
        public int LastContactDay;
        public string LastOutcome;

        public void ExposeData()
        {
            Scribe_Values.Look(ref FactionName, "factionName");
            Scribe_Values.Look(ref RaidsSent, "raidsSent", 0);
            Scribe_Values.Look(ref LostPawns, "lostPawns", 0);
            Scribe_Values.Look(ref LastContactDay, "lastContactDay", 0);
            Scribe_Values.Look(ref LastOutcome, "lastOutcome");
        }
    }
}
