using System.Collections.Generic;
namespace ClaudeStoryteller.Models
{
    public class ColonyState
    {
        public string RequestId { get; set; }
        public string CallType { get; set; }
        public ColonyInfo Colony { get; set; }
        public CombatReadiness CombatReadiness { get; set; }
        public Resources Resources { get; set; }
        public RecentHistory RecentHistory { get; set; }
        public Dictionary<string, int> Cooldowns { get; set; }
        public List<string> AvailableFactions { get; set; }
        // name -> "hostile, goodwill -100, tribal" style summary for every faction in
        // AvailableFactions (except the synthetic "Mechanoid" entry, whose real faction is Hidden
        // and has no name to key on).
        public Dictionary<string, string> FactionDetails { get; set; }
        public List<string> DoNotRepeat { get; set; }
        public QueueContext CurrentQueue { get; set; }
        public ArcHistorySummary ArcHistory { get; set; }
        public DifficultyInfo Difficulty { get; set; }
        public Dictionary<string, List<string>> AvailableEvents { get; set; }
        // defName -> label, for mod-added events only. Empty on a vanilla-only modlist.
        public Dictionary<string, string> EventGlossary { get; set; }
        public EventDensity Density { get; set; }
        public string LastPosture { get; set; }
        public List<string> HighlightedEvents { get; set; }
        public List<string> ExcludedThisCall { get; set; }
        public string StorytellingMood { get; set; }
        public Dictionary<string, int> CategoryUsageLast5 { get; set; }
        public int RandomSeed { get; set; }
        public List<string> UninvitedIncidents { get; set; }
        public ArcProgress ArcProgress { get; set; }         // null when no arc is active
        public List<string> RecurringFactions { get; set; }  // from the faction ledger

        // Phase 3: names, so letters can be about someone. Deterministic order (by thingID).
        public List<string> ColonistNames { get; set; }
        // "joined: X; died: Y; downed: Z" since the previous unified call, or null when nothing changed.
        public string CastChangesSinceLastCall { get; set; }

        // defName -> short mechanical gloss, for every root-selectable QuestScriptDef that
        // CanRun right now. Use one as a beat/scattered event's "type" like an incident defName.
        public Dictionary<string, string> AvailableQuests { get; set; }
    }
    public class ColonyInfo
    {
        public string Name { get; set; }
        public int DaysSurvived { get; set; }
        public string Phase { get; set; }
        public string NarrativeState { get; set; }
        public int ColonistCount { get; set; }
        public float Wealth { get; set; }
        public float RaidPoints { get; set; }
        public float AdaptationScore { get; set; }
        public float ThreatScale { get; set; }
    }
    public class CombatReadiness
    {
        public float Score { get; set; }
        public string MeleeStrength { get; set; }
        public string RangedStrength { get; set; }
        public List<string> Defenses { get; set; }
        public List<string> Vulnerabilities { get; set; }
    }
    public class Resources
    {
        public int FoodDays { get; set; }
        public string Medicine { get; set; }
        public string Components { get; set; }
        public int Silver { get; set; }
    }
    public class RecentHistory
    {
        public int DaysSinceThreat { get; set; }
        public int DaysSinceColonistDeath { get; set; }
        public int DaysSinceColonistDowned { get; set; }
        public bool ThreatActiveNow { get; set; }
        public int ColonistDeathsTotal { get; set; }
        public int ColonistDownedTotal { get; set; }
        public string LastColonistDeathName { get; set; }
        public List<PastEvent> LastEvents { get; set; }
    }
    public class PastEvent
    {
        public string Type { get; set; }
        public int DaysAgo { get; set; }
        public string Outcome { get; set; }
        public string RequestedType { get; set; }
        public string Source { get; set; }
    }
    public class QueueContext
    {
        public int PendingCount { get; set; }
        public List<string> QueuedTypes { get; set; }
        public string QueueSummary { get; set; }
    }
    public class ArcHistorySummary
    {
        public int TotalArcs { get; set; }
        public List<ArcSummaryEntry> Last3Arcs { get; set; }
        public List<string> OverusedEvents { get; set; }
        public List<string> OverusedOpeners { get; set; }
        public List<string> UnderusedEvents { get; set; }
        public string DominantPattern { get; set; }
        public string Instruction { get; set; }
    }
    public class ArcSummaryEntry
    {
        public string Name { get; set; }
        public List<string> Events { get; set; }
        public string Outcome { get; set; }
        public int Day { get; set; }
        public string Question { get; set; }
        public string Summary { get; set; }
        public int Deaths { get; set; }
        public int ColonistDelta { get; set; }
        public List<string> Links { get; set; }
        public string Faction { get; set; }
        public List<string> UnresolvedThreads { get; set; }
    }
    public class DifficultyInfo
    {
        public string Label { get; set; }
        public float ThreatScale { get; set; }
        public float MaxIntensity { get; set; }
        public float MinIntensity { get; set; }
        public bool AllowThreats { get; set; }
        public bool AllowMajorThreats { get; set; }
    }

    // ========== Arc progress (Phase 2, request-side only, never Scribe'd) ==========

    public class ArcBaseline
    {
        public int Colonists { get; set; }
        public float Wealth { get; set; }
        public int FoodDays { get; set; }
        public string Medicine { get; set; }
    }

    public class ArcDeltas
    {
        public int Days { get; set; }
        public int Colonists { get; set; }
        public float Wealth { get; set; }
        public int FoodDays { get; set; }
        public int Deaths { get; set; }
        public int Downed { get; set; }
        public string MedicineNow { get; set; }
    }

    public class ArcBeatChanges
    {
        public int Colonists { get; set; }
        public float Wealth { get; set; }
        public int FoodDays { get; set; }
        public int Deaths { get; set; }
        public int Downed { get; set; }
    }

    public class ArcBeatSummary
    {
        public string Type { get; set; }
        public string RequestedType { get; set; }
        public string Outcome { get; set; }
        public string CircleStep { get; set; }
        public string Link { get; set; }
        public int Day { get; set; }
        public ArcBeatChanges ChangesAfter { get; set; }
    }

    public class QueuedBeatSummary
    {
        public string Type { get; set; }
        public string CircleStep { get; set; }
        public string Link { get; set; }
        public float FiresInHours { get; set; }
        public string FireWhen { get; set; }
    }

    public class ArcProgress
    {
        public string Name { get; set; }
        public string StoryQuestion { get; set; }
        public string ArcFaction { get; set; }
        public int DayStarted { get; set; }
        public int DaysActive { get; set; }
        public string SummarySoFar { get; set; }
        public string LastExpectation { get; set; }
        public ArcBaseline Baseline { get; set; }
        public ArcDeltas NowVsBaseline { get; set; }
        public List<ArcBeatSummary> BeatsFired { get; set; }
        public ArcDeltas SinceLastBeat { get; set; }
        public List<QueuedBeatSummary> QueuedBeats { get; set; }
        public List<string> StepsUsed { get; set; }
        public int CallsSinceBeatAuthored { get; set; }
        public bool ClosingPending { get; set; }
    }
}
