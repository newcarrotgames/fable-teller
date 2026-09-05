using System.Linq;
using LudeonTK;
using RimWorld;
using Verse;

namespace ClaudeStoryteller
{
    /// <summary>
    /// Dev-mode debug actions (Misc tab, or wherever LudeonTK files this category) to make
    /// verifying the storyteller practical without waiting real minutes for a call or an arc
    /// to age out. Attribute shape confirmed by compiling against the installed Assembly-CSharp
    /// (grep -a -c "DebugAction" / "LudeonTK" both >0) — if a future RimWorld version changes
    /// the attribute's constructor, the compiler catches it here, not at runtime.
    /// </summary>
    public static class DebugActions
    {
        [DebugAction("Claude Storyteller", "Force unified call now", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceUnifiedCallNow()
        {
            var comp = StorytellerGameComponent.Get();
            if (comp == null)
            {
                Log.Warning("[ClaudeStoryteller] No StorytellerGameComponent; cannot force a call.");
                return;
            }

            // Push the requested tick far enough into the past that the comp's own
            // "floorTick = lastCallTick + minGapTicks" clamp is the only thing still gating it,
            // and set lastCallTick back too so callMinDays does not block this from firing on
            // the very next MakeIntervalIncidents pass.
            int farPast = System.Math.Max(0, Find.TickManager.TicksGame - GenDate.TicksPerDay * 30);
            comp.SetLastUnifiedCallTick(farPast);
            ClaudeLogger.LogEntry("DEBUG_ACTION", "Forced unified call: next MakeIntervalIncidents pass should fire it.");
        }

        [DebugAction("Claude Storyteller", "Print arc state", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PrintArcState()
        {
            var comp = StorytellerGameComponent.Get();
            if (comp == null)
            {
                Log.Message("[ClaudeStoryteller] No StorytellerGameComponent.");
                return;
            }

            if (string.IsNullOrEmpty(comp.ActiveArcName))
            {
                ClaudeLogger.LogEntry("DEBUG_ACTION", $"No active arc. Arcs logged so far: {comp.ArcCount}.");
                return;
            }

            ClaudeLogger.LogEntry("DEBUG_ACTION",
                $"Arc '{comp.ActiveArcName}'. Question: {comp.ActiveArcQuestion}. Faction: {comp.ActiveArcFaction ?? "none"}. " +
                $"Started day {comp.GetActiveArcStartDay()}. Queued beats: {comp.QueuedArcBeats}. " +
                $"Deaths: {comp.ColonistDeathsTotal}. Downed: {comp.ColonistDownedTotal}.");
        }

        [DebugAction("Claude Storyteller", "Abandon current arc", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AbandonCurrentArc()
        {
            var comp = StorytellerGameComponent.Get();
            if (comp == null || string.IsNullOrEmpty(comp.ActiveArcName))
            {
                Log.Message("[ClaudeStoryteller] No active arc to abandon.");
                return;
            }

            EventQueue.ClearBySource("narrative");
            comp.FinalizeArc("abandoned");
        }

        [DebugAction("Claude Storyteller", "Simulate colonist death", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SimulateColonistDeath()
        {
            var comp = StorytellerGameComponent.Get();
            var pawn = Find.CurrentMap?.mapPawns?.FreeColonists?.FirstOrDefault();
            if (comp == null || pawn == null)
            {
                Log.Message("[ClaudeStoryteller] No free colonist to simulate a death for.");
                return;
            }

            comp.RecordColonistDeath(pawn.Name?.ToStringShort ?? pawn.LabelShortCap);
        }

        [DebugAction("Claude Storyteller", "Simulate colonist downed", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SimulateColonistDowned()
        {
            var comp = StorytellerGameComponent.Get();
            var pawn = Find.CurrentMap?.mapPawns?.FreeColonists?.FirstOrDefault();
            if (comp == null || pawn == null)
            {
                Log.Message("[ClaudeStoryteller] No free colonist to simulate a downed for.");
                return;
            }

            comp.RecordColonistDowned(pawn.Name?.ToStringShort ?? pawn.LabelShortCap);
        }
    }
}
