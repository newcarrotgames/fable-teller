using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ClaudeStoryteller
{
    /// <summary>
    /// Tracks two things vanilla never told us: real colonist deaths/downs (so
    /// days_since_colonist_death stops being a fabricated sentinel), and incidents that fired
    /// without our involvement ("uninvited" — vanilla BaseStoryteller comps still run quests,
    /// deep-drill infestations, Odyssey signals, etc. alongside us).
    ///
    /// Both hooks are resolved by name/shape, never a hardcoded signature — the exact parameter
    /// types of Notify_PawnEvent's second argument were not independently confirmed against the
    /// installed assembly, so the postfix reads it via ToString() (enum values print their
    /// member name) rather than binding to a compile-time enum type. Fails soft: if a method or
    /// member cannot be resolved, that half of the patch is simply inactive and logged.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PawnEventPatch
    {
        public static bool PatchActive { get; private set; }

        // Dedupe: a single beat window should not double-count the same pawn's death/downed
        // if the notify hook fires more than once for one incident resolution.
        private static int lastDeathThingId = -1;
        private static int lastDownedThingId = -1;
        private static int lastJoinedThingId = -1;

        // Cached reflection lookups for reading FiringIncident.source without a compile-time
        // dependency on the exact member name/kind (field vs property).
        private static FieldInfo firingIncidentSourceField;
        private static PropertyInfo firingIncidentSourceProperty;

        private static readonly List<string> uninvitedIncidents = new List<string>();
        private static readonly object uninvitedLock = new object();
        private const int MAX_UNINVITED = 10;

        static PawnEventPatch()
        {
            bool notifyPatched = false;
            bool tryFirePatched = false;
            bool popAdaptationPatched = false;

            try
            {
                var harmony = new Harmony("nathandidier.claudestoryteller.pawnevent");

                // Notify_PawnEvent(Pawn, <enum>, DamageInfo?) on Storyteller — resolve by name
                // and first-parameter shape, mirroring LetterFlavorPatch's pattern.
                MethodInfo notifyTarget = typeof(Storyteller)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Notify_PawnEvent"
                        && m.GetParameters().Length >= 2
                        && typeof(Pawn).IsAssignableFrom(m.GetParameters()[0].ParameterType));

                if (notifyTarget != null)
                {
                    harmony.Patch(notifyTarget, postfix: new HarmonyMethod(
                        typeof(PawnEventPatch).GetMethod(nameof(NotifyPawnEventPostfix),
                            BindingFlags.NonPublic | BindingFlags.Static)));
                    notifyPatched = true;
                    Log.Message($"[ClaudeStoryteller] Pawn event patch applied to {notifyTarget}.");
                }
                else
                {
                    Log.Warning("[ClaudeStoryteller] Storyteller.Notify_PawnEvent not found; " +
                                "colonist death/downed tracking disabled.");
                }

                // TryFire(FiringIncident, ...) on Storyteller — resolve by name and
                // FiringIncident-typed first parameter, returning bool.
                MethodInfo tryFireTarget = typeof(Storyteller)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "TryFire"
                        && m.GetParameters().Length >= 1
                        && typeof(FiringIncident).IsAssignableFrom(m.GetParameters()[0].ParameterType)
                        && m.ReturnType == typeof(bool));

                if (tryFireTarget != null)
                {
                    firingIncidentSourceField = AccessTools.Field(typeof(FiringIncident), "source");
                    if (firingIncidentSourceField == null)
                        firingIncidentSourceProperty = AccessTools.Property(typeof(FiringIncident), "source")
                            ?? AccessTools.Property(typeof(FiringIncident), "Source");

                    if (firingIncidentSourceField == null && firingIncidentSourceProperty == null)
                    {
                        Log.Warning("[ClaudeStoryteller] FiringIncident.source could not be resolved by " +
                                    "reflection (neither field nor property); uninvited-incident tracking is inactive.");
                    }

                    harmony.Patch(tryFireTarget, postfix: new HarmonyMethod(
                        typeof(PawnEventPatch).GetMethod(nameof(TryFirePostfix),
                            BindingFlags.NonPublic | BindingFlags.Static)));
                    tryFirePatched = true;
                    Log.Message($"[ClaudeStoryteller] TryFire patch applied to {tryFireTarget}.");
                }
                else
                {
                    Log.Warning("[ClaudeStoryteller] Storyteller.TryFire not found; " +
                                "uninvited-incident tracking disabled.");
                }

                // Optional (Phase 3): StoryWatcher_PopAdaptation.Notify_PawnEvent(Pawn,
                // PopAdaptationEvent) -> RecordColonistJoined on GainedColonist. Same
                // resolve-by-name-and-shape, fail-soft pattern as the two patches above.
                MethodInfo popAdaptTarget = typeof(StoryWatcher_PopAdaptation)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Notify_PawnEvent"
                        && m.GetParameters().Length >= 2
                        && typeof(Pawn).IsAssignableFrom(m.GetParameters()[0].ParameterType));

                if (popAdaptTarget != null)
                {
                    harmony.Patch(popAdaptTarget, postfix: new HarmonyMethod(
                        typeof(PawnEventPatch).GetMethod(nameof(PopAdaptationPostfix),
                            BindingFlags.NonPublic | BindingFlags.Static)));
                    popAdaptationPatched = true;
                    Log.Message($"[ClaudeStoryteller] Pop adaptation patch applied to {popAdaptTarget}.");
                }
                else
                {
                    Log.Warning("[ClaudeStoryteller] StoryWatcher_PopAdaptation.Notify_PawnEvent not found; " +
                                "colonist-joined tracking disabled.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] Pawn event patch failed: {ex.Message}.");
            }

            PatchActive = notifyPatched || tryFirePatched || popAdaptationPatched;
        }

        // __0 = Pawn, __1 = PopAdaptationEvent. Compile-time enum confirmed against the
        // installed Assembly-CSharp.dll the same way as AdaptationEvent above.
        private static void PopAdaptationPostfix(Pawn __0, PopAdaptationEvent __1)
        {
            try
            {
                if (__0 == null || !__0.IsColonist) return;
                if (__1 != PopAdaptationEvent.GainedColonist) return;

                if (__0.thingIDNumber == lastJoinedThingId) return;
                lastJoinedThingId = __0.thingIDNumber;
                StorytellerGameComponent.Get()?.RecordColonistJoined(__0.Name?.ToStringShort ?? __0.LabelShortCap);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] PopAdaptationPostfix failed: {ex.Message}");
            }
        }

        // __0 = Pawn, __1 = AdaptationEvent. Compile-time enum confirmed against the installed
        // Assembly-CSharp.dll (RimWorld.AdaptationEvent has Died/Downed members) — the compiler
        // is ground truth here, so this replaces the earlier ToString()-based reflection read.
        private static void NotifyPawnEventPostfix(Pawn __0, AdaptationEvent __1)
        {
            try
            {
                if (__0 == null || !__0.IsColonist) return;

                if (__1 == AdaptationEvent.Died)
                {
                    if (__0.thingIDNumber == lastDeathThingId) return;
                    lastDeathThingId = __0.thingIDNumber;
                    StorytellerGameComponent.Get()?.RecordColonistDeath(__0.LabelShortCap);
                }
                else if (__1 == AdaptationEvent.Downed)
                {
                    if (__0.thingIDNumber == lastDownedThingId) return;
                    lastDownedThingId = __0.thingIDNumber;
                    StorytellerGameComponent.Get()?.RecordColonistDowned(__0.LabelShortCap);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] NotifyPawnEventPostfix failed: {ex.Message}");
            }
        }

        // __0 = FiringIncident, __result = whether it actually fired.
        private static void TryFirePostfix(FiringIncident __0, bool __result)
        {
            try
            {
                if (__0 == null || !__result) return;

                object source = null;
                if (firingIncidentSourceField != null) source = firingIncidentSourceField.GetValue(__0);
                else if (firingIncidentSourceProperty != null) source = firingIncidentSourceProperty.GetValue(__0);

                bool fromClaude = source is StorytellerComp_Claude;
                if (fromClaude) return; // our own events are already recorded at the call site

                if (source != null) // only record when we could actually confirm it wasn't ours
                    RecordUninvitedIncident(__0.def?.defName ?? "unknown");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] TryFirePostfix failed: {ex.Message}");
            }
        }

        private static void RecordUninvitedIncident(string defName)
        {
            lock (uninvitedLock)
            {
                uninvitedIncidents.Add(defName);
                while (uninvitedIncidents.Count > MAX_UNINVITED)
                    uninvitedIncidents.RemoveAt(0);
            }
            ClaudeLogger.LogEntry("UNINVITED_INCIDENT", $"{defName} fired outside our control.");
        }

        public static List<string> GetUninvitedIncidents()
        {
            lock (uninvitedLock) { return new List<string>(uninvitedIncidents); }
        }

        public static void ClearUninvitedIncidents()
        {
            lock (uninvitedLock) { uninvitedIncidents.Clear(); }
        }

        /// <summary>
        /// Clears every static dedupe/tracking field. These statics outlive a Game (the class is
        /// [StaticConstructorOnStartup]), so without this a second colony in the same session
        /// inherits the first colony's last-seen death/downed/joined pawn ids and uninvited list.
        /// Call from StorytellerGameComponent's constructor, next to EventQueue.Clear().
        /// </summary>
        public static void ResetSession()
        {
            lastDeathThingId = -1;
            lastDownedThingId = -1;
            lastJoinedThingId = -1;
            lock (uninvitedLock) { uninvitedIncidents.Clear(); }
        }
    }
}
