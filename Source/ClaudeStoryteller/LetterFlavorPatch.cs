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
    /// Merges Claude's player-facing flavor text into the vanilla letter an incident
    /// already sends, so one event produces one letter instead of two.
    ///
    /// Flow: before firing an incident we stash its flavor here. The next letter the
    /// game raises absorbs it. If no letter appears within FLAVOR_WINDOW_TICKS - some
    /// incidents send none - the comp calls TakeExpiredFlavor() and it goes out standalone.
    ///
    /// Pending flavor is a FIFO QUEUE, not a single slot: several letters can be produced in
    /// one MakeIntervalIncidents pass (letter-only "none" beats, the arc closing letter, more
    /// than one incident firing in the same pass), and a single slot meant the second staging
    /// call clobbered the first before any letter had a chance to absorb it. Entries are aged
    /// from their own SetTick, and because the queue is FIFO by staging order, the front entry
    /// is always the oldest — if it has not expired, nothing behind it can have either.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class LetterFlavorPatch
    {
        private class FlavorEntry
        {
            public string Flavor;
            public bool IsArc;
            public int SetTick;
            public string Label;
            public bool IsThreat;
        }

        private static readonly Queue<FlavorEntry> pendingFlavors = new Queue<FlavorEntry>();
        private static readonly object pendingLock = new object();

        // A letter raised within this many ticks of the incident firing is treated as
        // that incident's letter. Generous enough for same-tick delivery, short enough
        // not to attach to an unrelated letter later.
        private const int FLAVOR_WINDOW_TICKS = 30;

        public static bool PatchActive { get; private set; }

        private static LetterDef narrativeLetter;
        private static bool narrativeLetterResolved;

        /// <summary>The mod's violet letter color (Defs\LetterDefs\ClaudeLetters.xml).
        /// Resolved lazily at first letter, never from the static ctor; null if the
        /// XML failed to load, in which case callers keep NeutralEvent.</summary>
        public static LetterDef NarrativeLetter
        {
            get
            {
                if (!narrativeLetterResolved)
                {
                    narrativeLetterResolved = true;
                    narrativeLetter = DefDatabase<LetterDef>.GetNamedSilentFail("ClaudeStoryteller_Narrative");
                }
                return narrativeLetter;
            }
        }

        // Set (and cleared in a finally) around Find.LetterStack.ReceiveLetter calls the mod
        // makes itself (SendNarrativeLetter). Letters we send ourselves are already the flavor
        // text — they must never additionally absorb a DIFFERENT staged flavor entry meant for
        // some other letter.
        public static bool SelfSendActive;

        // Depth-counted flag for "Storyteller.TryFire is currently executing a Claude-sourced
        // incident". StageFlavor is called optimistically, before our FiringIncident is handed
        // back to vanilla to actually fire — so without this gate, ANY letter that lands within
        // FLAVOR_WINDOW_TICKS (a vanilla BaseStoryteller incident, another mod's incident, an
        // "uninvited" incident per PawnEventPatch) would absorb flavor written about a totally
        // different pawn/event. Depth-counted rather than bool in case TryFire ever reenters.
        private static int expectingOwnLetterDepth;

        static LetterFlavorPatch()
        {
            try
            {
                // Resolve the overload by shape rather than a hardcoded signature:
                // the one taking a Letter is what every other overload funnels into.
                MethodInfo target = typeof(LetterStack)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "ReceiveLetter"
                        && m.GetParameters().Length > 0
                        && typeof(Letter).IsAssignableFrom(m.GetParameters()[0].ParameterType));

                if (target == null)
                {
                    Log.Warning("[ClaudeStoryteller] LetterStack.ReceiveLetter(Letter) not found; "
                              + "flavor will be sent as separate letters.");
                    return;
                }

                var harmony = new Harmony("nathandidier.claudestoryteller.letterflavor");
                harmony.Patch(target, prefix: new HarmonyMethod(
                    typeof(LetterFlavorPatch).GetMethod(nameof(Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static)));

                // Resolved by name/shape, same pattern as PawnEventPatch's TryFire lookup —
                // TryFire is the one place vanilla actually executes a FiringIncident (and,
                // synchronously in nearly every case, sends its letter), so wrapping it tells
                // us whose letter is about to be raised.
                MethodInfo tryFireTarget = typeof(Storyteller)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "TryFire"
                        && m.GetParameters().Length >= 1
                        && typeof(FiringIncident).IsAssignableFrom(m.GetParameters()[0].ParameterType)
                        && m.ReturnType == typeof(bool));

                if (tryFireTarget != null)
                {
                    harmony.Patch(tryFireTarget,
                        prefix: new HarmonyMethod(typeof(LetterFlavorPatch).GetMethod(
                            nameof(TryFirePrefix), BindingFlags.NonPublic | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(LetterFlavorPatch).GetMethod(
                            nameof(TryFireFinalizer), BindingFlags.NonPublic | BindingFlags.Static)));
                }
                else
                {
                    Log.Warning("[ClaudeStoryteller] Storyteller.TryFire not found; letter merge "
                              + "will not verify the letter belongs to our own incident.");
                }

                PatchActive = true;
                Log.Message($"[ClaudeStoryteller] Letter merge patch applied to {target.Name}"
                          + $"({target.GetParameters()[0].ParameterType.Name}).");
            }
            catch (Exception ex)
            {
                PatchActive = false;
                Log.Warning($"[ClaudeStoryteller] Letter merge patch failed: {ex.Message}. "
                          + "Flavor will be sent as separate letters.");
            }
        }

        private static void TryFirePrefix(FiringIncident __0)
        {
            if (__0?.source is StorytellerComp_Claude) expectingOwnLetterDepth++;
        }

        private static void TryFireFinalizer(FiringIncident __0)
        {
            if (__0?.source is StorytellerComp_Claude) expectingOwnLetterDepth--;
        }

        /// <summary>Stash flavor for the next letter the game raises. isArc: true for narrative
        /// beats, so the merged (or, if never absorbed, standalone) body can be recorded into
        /// the active arc's STORY_TRANSCRIPT. label/isThreat travel with the entry so the
        /// standalone fallback (TakeExpiredFlavor) can send it without a side channel back to
        /// the comp.</summary>
        public static void StageFlavor(string flavor, bool isArc, string label, bool isThreat)
        {
            if (!PatchActive || string.IsNullOrEmpty(flavor)) return;
            lock (pendingLock)
            {
                pendingFlavors.Enqueue(new FlavorEntry
                {
                    Flavor = flavor,
                    IsArc = isArc,
                    SetTick = Find.TickManager?.TicksGame ?? 0,
                    Label = label,
                    IsThreat = isThreat
                });
            }
        }

        /// <summary>
        /// Called each pass by the comp. If the OLDEST staged flavor was never absorbed by a
        /// letter, pop and return it so the narration is not silently lost. Because the queue is
        /// FIFO by staging order, the front entry is always the oldest, so a single check here
        /// (rather than scanning) is sufficient — if the front has not expired, nothing behind it
        /// has either.
        /// </summary>
        public static string TakeExpiredFlavor(out bool isArc, out string label, out bool isThreat)
        {
            lock (pendingLock)
            {
                isArc = false; label = null; isThreat = false;
                if (pendingFlavors.Count == 0) return null;

                var entry = pendingFlavors.Peek();
                int now = Find.TickManager?.TicksGame ?? 0;
                if (now - entry.SetTick < FLAVOR_WINDOW_TICKS) return null;

                pendingFlavors.Dequeue();
                isArc = entry.IsArc;
                label = entry.Label;
                isThreat = entry.IsThreat;
                return entry.Flavor;
            }
        }

        public static void ClearPending()
        {
            lock (pendingLock)
            {
                pendingFlavors.Clear();
            }
        }

        // __0 is the first positional argument, so this does not depend on the
        // parameter being named "let" in a given RimWorld version.
        private static void Prefix(Letter __0)
        {
            if (__0 == null) return;
            // Letters the mod sends itself (SendNarrativeLetter) are already the flavor text —
            // never let them additionally swallow a DIFFERENT entry meant for some other letter.
            if (SelfSendActive) return;
            // Not inside our own incident's TryFire: this letter belongs to someone else
            // (vanilla BaseStoryteller, another mod, an "uninvited" incident) and must not
            // absorb flavor written about a different pawn/event. Leave it queued — either our
            // own incident's letter claims it shortly after, or it expires and goes out
            // standalone via TakeExpiredFlavor.
            if (expectingOwnLetterDepth <= 0) return;

            FlavorEntry entry;
            lock (pendingLock)
            {
                if (pendingFlavors.Count == 0) return;
                entry = pendingFlavors.Peek();
                int now = Find.TickManager?.TicksGame ?? 0;
                // Oldest entry not yet expired: take it. If it HAS expired, leave it for
                // TakeExpiredFlavor rather than silently dropping it here.
                if (now - entry.SetTick > FLAVOR_WINDOW_TICKS) return;
                pendingFlavors.Dequeue();
            }

            try
            {
                // ChoiceLetter carries the body text; plain Letter subclasses may not.
                FieldInfo textField = AccessTools.Field(__0.GetType(), "text");
                if (textField == null || textField.FieldType != typeof(TaggedString))
                {
                    // Nothing to append to - put it back at the FRONT so ordering is preserved
                    // and the standalone path (TakeExpiredFlavor) sends it once it expires.
                    lock (pendingLock)
                    {
                        var items = new List<FlavorEntry>(pendingFlavors);
                        items.Insert(0, entry);
                        pendingFlavors.Clear();
                        foreach (var it in items) pendingFlavors.Enqueue(it);
                    }
                    return;
                }

                var existing = (TaggedString)textField.GetValue(__0);
                string body = existing.RawText ?? "";
                string mergedBody = body.TrimEnd() + Environment.NewLine + Environment.NewLine + entry.Flavor;
                textField.SetValue(__0, (TaggedString)mergedBody);

                // Grey (NeutralEvent) letters carrying Claude's narration get the mod's
                // color instead. Only grey: threat/negative defs keep their vanilla
                // colors and sounds — danger signaling must survive the recolor.
                if (__0.def == LetterDefOf.NeutralEvent && NarrativeLetter != null)
                    __0.def = NarrativeLetter;

                ClaudeLogger.LogEntry("LETTER_MERGED",
                    $"Flavor merged into letter '{__0.Label}': {entry.Flavor}");

                StorytellerComp_Claude.CheckNameUnverified(entry.Flavor);
                if (entry.IsArc) StorytellerGameComponent.Get()?.RecordArcLetter(mergedBody);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] Letter merge failed: {ex.Message}");
            }
        }
    }
}
