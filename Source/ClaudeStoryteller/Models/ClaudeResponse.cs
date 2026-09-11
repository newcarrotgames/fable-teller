using System.Collections.Generic;
namespace ClaudeStoryteller.Models
{
    // ========== Unified Response (one API call handles everything) ==========

    public class UnifiedResponse
    {
        // Narrative arc (optional — only when Claude wants to start one)
        public NarrativeArcDecision Arc { get; set; }

        // Scattered events — the world happening around/during the arc
        // 0 to 15+ events, Claude decides how many based on difficulty and colony state
        public List<ScatteredEvent> ScatteredEvents { get; set; }

        // Timer adjustments (clamped by code before applying)
        public TimerAdjustment AdjustTimers { get; set; }

        // Claude's current storytelling style blend
        public StorytellingPosture Posture { get; set; }

        // How many days until the next unified call
        public float NextCallDays { get; set; }

        // Overall reasoning for this call's decisions (top-level "overall_reasoning" key)
        public string OverallReasoning { get; set; }
    }

    public class ScatteredEvent
    {
        public ScatteredEvent()
        {
            Intensity = 1.0f;
            DelayHours = 0;
        }
        public float DelayHours { get; set; }
        public string Type { get; set; }
        public string Subtype { get; set; }
        public string Faction { get; set; }
        public float Intensity { get; set; }
        public string Animal { get; set; }
        public string Note { get; set; }          // debug log only
        public string Flavor { get; set; }        // shown to the player

        // "walk_in" | "walk_in_groups" | "drop_edge" | "drop_center" | "drop_scatter" | null
        // (game decides). Raids only; read from this event's own JSON sub-block, never the
        // whole document — same rule as fire_when.
        public string ArrivalMode { get; set; }

        // Only two workers honor these (verified against the decompiled 1.6 assembly):
        // manhunter packs (kind and/or count) and raids (kind requires count >= 1).
        // Read from this event's own JSON sub-block only.
        public string PawnKind { get; set; }
        public int PawnCount { get; set; }
    }

    public class EventDecision
    {
        public string Decision { get; set; } // "fire_event", "wait", "send_help"
        public EventChoice Event { get; set; }
        public string Reasoning { get; set; }
        public string NarrativeIntent { get; set; }
    }

    public class NarrativeArcDecision
    {
        public string Decision { get; set; } // "start_arc", "continue", "end_arc", "skip"
        public string ArcName { get; set; }
        public string ArcFaction { get; set; }      // exact faction name, "same_as_opening", or null
        public string StoryQuestion { get; set; }   // start_arc; the in-world question the arc answers
        public string ClosingFlavor { get; set; }   // end_arc only; shown to the player
        public List<ArcEvent> Events { get; set; }
        public string Reasoning { get; set; }     // debug log only (parsed from "arc_reasoning")
        public string ArcFlavor { get; set; }     // shown to the player

        // ---- Phase 2 ----
        public List<string> ArcReservedTypes { get; set; }  // scattered events of these types are dropped while active
        public string QueuedBeatsAction { get; set; }       // "keep" | "replace"
        public string ArcSummarySoFar { get; set; }          // 2-3 sentences, Claude's own memory
        public List<string> UnresolvedThreads { get; set; }  // end_arc; 1-2 debug/memory items
    }

    public class StorytellingPosture
    {
        public string CurrentBlend { get; set; }  // e.g. "cassandra-heavy with randy spice"
        public string Reasoning { get; set; }
        public string NextPostureHint { get; set; } // what might change next
    }

    // ========== Event Types (unchanged) ==========

    public class EventChoice
    {
        public EventChoice()
        {
            Intensity = 1.0f;
            DelayHours = 0;
        }
        public string Category { get; set; }
        public string Type { get; set; }
        public string Subtype { get; set; }
        public string Faction { get; set; }
        public float Intensity { get; set; }
        public int DelayHours { get; set; }
        public string Animal { get; set; }
        public string Note { get; set; }          // debug log only
        public string Flavor { get; set; }        // shown to the player
    }

    public class ArcEvent
    {
        public ArcEvent()
        {
            Intensity = 1.0f;
            DelayHours = 0;
        }
        public float DelayHours { get; set; }
        public string Type { get; set; }          // may be "none" (letter-only beat)
        public string Subtype { get; set; }
        public string Faction { get; set; }       // exact name, "same_as_opening", or null
        public float Intensity { get; set; }
        public string Animal { get; set; }
        public string Note { get; set; }          // debug log only
        public string Flavor { get; set; }        // shown to the player

        // ---- Phase 2 (all read from the per-beat eventJson sub-block) ----
        public string CircleStep { get; set; }    // "need" | "search" | "find" | "take" | "return" | "change"
        public string Link { get; set; }          // "and" | "but" | "therefore"
        public string LinkReason { get; set; }    // debug log only
        public string Expect { get; set; }        // debug log only
        public string FireWhen { get; set; }       // "scheduled" | "after_calm"

        // "walk_in" | "walk_in_groups" | "drop_edge" | "drop_center" | "drop_scatter" | null
        // (game decides). Raids only; read from this beat's own eventJson sub-block only.
        public string ArrivalMode { get; set; }

        // Only two workers honor these (verified against the decompiled 1.6 assembly):
        // manhunter packs (kind and/or count) and raids (kind requires count >= 1).
        // Read from this beat's own eventJson sub-block only.
        public string PawnKind { get; set; }
        public int PawnCount { get; set; }

        // Raw braced JSON of the optional "on_bad" sub-object (type/subtype/faction/intensity/flavor),
        // kept unparsed here and only decoded via ClaudeApiClient's static extractors at fire time —
        // it applies to the NEXT beat only, so there is no value in eagerly building a typed object.
        public string OnBadJson { get; set; }
    }

    // ========== Timer Adjustment (clamped in code) ==========

    public class TimerAdjustment
    {
        public float MinorMinHours { get; set; }
        public float MinorMaxHours { get; set; }
        public float MajorMinDays { get; set; }
        public float MajorMaxDays { get; set; }
        public float NarrativeMinDays { get; set; }
        public float NarrativeMaxDays { get; set; }
    }

    // ========== Density Metrics (sent to Claude for awareness) ==========

    public class EventDensity
    {
        public int EventsLast7Days { get; set; }
        public int EventsLast15Days { get; set; }
        public int ThreatsLast7Days { get; set; }
        public int ThreatsLast15Days { get; set; }
        public int DiseasesLast30Days { get; set; }
        public int DaysSinceLastDisease { get; set; }
        public int DaysSinceArcCompleted { get; set; }
        public string ActiveArc { get; set; } // null if no arc running
        public int QueuedArcBeats { get; set; }
    }

    // ========== Vanilla Reference (static data for Claude) ==========

    public class VanillaReference
    {
        public VanillaStoryteller Cassandra { get; set; }
        public VanillaStoryteller Phoebe { get; set; }
        public VanillaStoryteller Randy { get; set; }
    }

    public class VanillaStoryteller
    {
        public string Style { get; set; }
        public float MiscMtbDays { get; set; }
        public float ThreatCycleDays { get; set; }
        public string ThreatsPerCycle { get; set; }
        public float RestPeriodDays { get; set; }
        public float MinThreatSpacingDays { get; set; }
        public float DiseaseApproxMtbDays { get; set; }
    }

    // ========== Legacy models kept for backwards compat ==========

    public class ClaudeResponse
    {
        public string Decision { get; set; }
        public EventChoice Event { get; set; }
        public string Reasoning { get; set; }
        public string NarrativeIntent { get; set; }
        public TimerAdjustment AdjustTimers { get; set; }
    }

    public class NarrativeArcResponse
    {
        public string ArcName { get; set; }
        public List<ArcEvent> Events { get; set; }
        public string Reasoning { get; set; }     // debug log only
        public string ArcFlavor { get; set; }     // shown to the player
        public TimerAdjustment AdjustTimers { get; set; }
    }
}
