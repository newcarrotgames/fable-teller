using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ClaudeStoryteller
{
    public class QueuedEvent : IExposable
    {
        public string EventType { get; set; }
        public string Category { get; set; }
        public string Subtype { get; set; }
        public string Faction { get; set; }
        public float Intensity { get; set; }
        public int FireAtTick { get; set; }
        public string SourceCycle { get; set; } // "minor", "major", "narrative"
        public string ArcName { get; set; }     // null unless part of a narrative arc
        public string Note { get; set; }
        public string Flavor { get; set; }

        // ---- Phase 2 ----
        public string Link { get; set; }         // "and" | "but" | "therefore"
        public string LinkReason { get; set; }   // debug log only
        public string CircleStep { get; set; }   // "need" | "search" | "find" | "take" | "return" | "change"
        public string Expect { get; set; }       // debug log only
        public string FireWhen { get; set; }      // "scheduled" | "after_calm"
        public string OnBadJson { get; set; }     // raw braced JSON of the optional on_bad block
        public string ArrivalMode { get; set; }   // "walk_in" | "walk_in_groups" | "drop_edge" | "drop_center" | "drop_scatter" | null
        public string PawnKindName { get; set; }  // manhunter packs and raids only; validated at fire time
        public int PawnCount { get; set; }        // 0 = let the game compute it

        public QueuedEvent()
        {
            Intensity = 1.0f;
        }

        public void ExposeData()
        {
            string eventType = EventType, category = Category, subtype = Subtype;
            string faction = Faction, sourceCycle = SourceCycle, arcName = ArcName;
            string note = Note, flavor = Flavor;
            string link = Link, linkReason = LinkReason, circleStep = CircleStep;
            string expect = Expect, fireWhen = FireWhen, onBadJson = OnBadJson;
            string arrivalMode = ArrivalMode, pawnKindName = PawnKindName;
            float intensity = Intensity;
            int fireAtTick = FireAtTick, pawnCount = PawnCount;

            Scribe_Values.Look(ref eventType, "eventType");
            Scribe_Values.Look(ref category, "category");
            Scribe_Values.Look(ref subtype, "subtype");
            Scribe_Values.Look(ref faction, "faction");
            Scribe_Values.Look(ref sourceCycle, "sourceCycle");
            Scribe_Values.Look(ref arcName, "arcName");
            Scribe_Values.Look(ref note, "note");
            Scribe_Values.Look(ref flavor, "flavor");
            Scribe_Values.Look(ref intensity, "intensity", 1.0f);
            Scribe_Values.Look(ref fireAtTick, "fireAtTick", 0);
            Scribe_Values.Look(ref link, "link");
            Scribe_Values.Look(ref linkReason, "linkReason");
            Scribe_Values.Look(ref circleStep, "circleStep");
            Scribe_Values.Look(ref expect, "expect");
            Scribe_Values.Look(ref fireWhen, "fireWhen");
            Scribe_Values.Look(ref onBadJson, "onBadJson");
            Scribe_Values.Look(ref arrivalMode, "arrivalMode");
            Scribe_Values.Look(ref pawnKindName, "pawnKindName");
            Scribe_Values.Look(ref pawnCount, "pawnCount", 0);

            EventType = eventType; Category = category; Subtype = subtype;
            Faction = faction; SourceCycle = sourceCycle; ArcName = arcName;
            Note = note; Flavor = flavor;
            Intensity = intensity; FireAtTick = fireAtTick;
            Link = link; LinkReason = linkReason; CircleStep = circleStep;
            Expect = expect; FireWhen = fireWhen; OnBadJson = onBadJson;
            ArrivalMode = arrivalMode; PawnKindName = pawnKindName; PawnCount = pawnCount;
        }
    }

    public static class EventQueue
    {
        private static readonly List<QueuedEvent> queue = new List<QueuedEvent>();
        private static readonly object lockObj = new object();

        // Conflict window: events of the same type within this many ticks get deduplicated
        private const int CONFLICT_WINDOW_TICKS = 4 * GenDate.TicksPerHour;

        // fire_when "after_calm" hold bookkeeping. Keyed by object reference — QueuedEvent has
        // no stable id, but the same instance stays in `queue` from Enqueue to Pop.
        private static readonly Dictionary<QueuedEvent, int> heldSinceTick = new Dictionary<QueuedEvent, int>();
        private static readonly HashSet<QueuedEvent> loggedHold = new HashSet<QueuedEvent>();
        private const int MAX_HOLD_TICKS = 48 * GenDate.TicksPerHour;

        public static void Enqueue(QueuedEvent evt)
        {
            lock (lockObj)
            {
                // Narrative arc events take priority — remove conflicting non-narrative events
                if (evt.SourceCycle == "narrative")
                {
                    queue.RemoveAll(q =>
                        q.SourceCycle != "narrative" &&
                        q.EventType == evt.EventType &&
                        Math.Abs(q.FireAtTick - evt.FireAtTick) < CONFLICT_WINDOW_TICKS
                    );
                }
                // Non-narrative events get skipped if a narrative event is already there
                else
                {
                    bool narrativeConflict = queue.Any(q =>
                        q.SourceCycle == "narrative" &&
                        q.EventType == evt.EventType &&
                        Math.Abs(q.FireAtTick - evt.FireAtTick) < CONFLICT_WINDOW_TICKS
                    );
                    if (narrativeConflict)
                    {
                        ClaudeLogger.LogEventSkipped(
                            $"Dedup: {evt.EventType} from {evt.SourceCycle} conflicts with queued narrative event"
                        );
                        return;
                    }
                }

                queue.Add(evt);
                queue.Sort((a, b) => a.FireAtTick.CompareTo(b.FireAtTick));

                ClaudeLogger.LogEntry("QUEUE_ADD",
                    $"Queued {evt.EventType} from {evt.SourceCycle} at tick {evt.FireAtTick}" +
                    (evt.ArcName != null ? $" (arc: {evt.ArcName})" : "")
                );
            }
        }

        public static void EnqueueDelayed(QueuedEvent evt, float delayHours)
        {
            evt.FireAtTick = Find.TickManager.TicksGame + (int)(delayHours * GenDate.TicksPerHour);
            Enqueue(evt);
        }

        /// <summary>
        /// Pops events whose fire time has arrived. An event with FireWhen == "after_calm" is
        /// held while threatActive() is true, up to MAX_HOLD_TICKS (48 game hours), after which
        /// it fires anyway. threatActive() is evaluated lazily and at most once per call — no
        /// point paying for GenHostility.AnyHostileActiveThreatToPlayer if nothing is due.
        /// </summary>
        public static List<QueuedEvent> PopReady(int currentTick, Func<bool> threatActive)
        {
            lock (lockObj)
            {
                var due = queue.Where(e => e.FireAtTick <= currentTick).ToList();
                if (due.Count == 0) return due;

                bool? threatIsActive = null;
                var ready = new List<QueuedEvent>();

                foreach (var evt in due)
                {
                    if (evt.FireWhen == "after_calm")
                    {
                        if (threatIsActive == null) threatIsActive = threatActive != null && threatActive();

                        if (threatIsActive == true)
                        {
                            if (!heldSinceTick.ContainsKey(evt)) heldSinceTick[evt] = currentTick;
                            int heldTicks = currentTick - heldSinceTick[evt];
                            if (heldTicks < MAX_HOLD_TICKS)
                            {
                                if (loggedHold.Add(evt))
                                    ClaudeLogger.LogEntry("ARC_HOLD",
                                        $"Holding {evt.EventType} (fire_when after_calm) while a threat is active.");
                                continue;
                            }
                            // Held past the max — release anyway rather than hold forever.
                        }
                    }
                    ready.Add(evt);
                }

                foreach (var evt in ready)
                {
                    queue.Remove(evt);
                    if (heldSinceTick.Remove(evt))
                        ClaudeLogger.LogEntry("ARC_RELEASED", $"Released {evt.EventType} from after_calm hold.");
                    loggedHold.Remove(evt);
                }

                return ready;
            }
        }

        public static List<QueuedEvent> PeekAll()
        {
            lock (lockObj)
            {
                return queue.ToList();
            }
        }

        public static int Count
        {
            get { lock (lockObj) { return queue.Count; } }
        }

        // Replaces the queue wholesale - used when restoring a save.
        public static void RestoreAll(List<QueuedEvent> restored)
        {
            lock (lockObj)
            {
                queue.Clear();
                heldSinceTick.Clear();
                loggedHold.Clear();
                if (restored != null)
                {
                    queue.AddRange(restored.Where(e => e != null && !string.IsNullOrEmpty(e.EventType)));
                    queue.Sort((a, b) => a.FireAtTick.CompareTo(b.FireAtTick));
                }
            }
        }

        public static void Clear()
        {
            lock (lockObj)
            {
                queue.Clear();
                heldSinceTick.Clear();
                loggedHold.Clear();
            }
        }

        public static void ClearBySource(string sourceCycle)
        {
            lock (lockObj)
            {
                queue.RemoveAll(e => e.SourceCycle == sourceCycle);
            }
        }

        /// <summary>Removes every still-queued narrative beat belonging to the named arc — used
        /// by FinalizeArc (so a finished arc's leftover beats never fire under a later arc) and
        /// by "queued_beats_action": "replace" (so new beats are not appended to stale ones).</summary>
        public static void ClearNarrativeFor(string arcName)
        {
            if (string.IsNullOrEmpty(arcName)) return;
            lock (lockObj)
            {
                queue.RemoveAll(e => e.SourceCycle == "narrative" && e.ArcName == arcName);
            }
        }

        public static int CountNarrativeFor(string arcName)
        {
            if (string.IsNullOrEmpty(arcName)) return 0;
            lock (lockObj)
            {
                return queue.Count(q => q.SourceCycle == "narrative" && q.ArcName == arcName);
            }
        }

        public static List<QueuedEvent> PeekNarrativeFor(string arcName)
        {
            if (string.IsNullOrEmpty(arcName)) return new List<QueuedEvent>();
            lock (lockObj)
            {
                return queue.Where(q => q.SourceCycle == "narrative" && q.ArcName == arcName)
                    .OrderBy(q => q.FireAtTick).ToList();
            }
        }

        public static List<string> GetQueuedTypes()
        {
            lock (lockObj)
            {
                return queue.Select(e => e.EventType).Distinct().ToList();
            }
        }

        public static string GetQueueSummary()
        {
            lock (lockObj)
            {
                if (queue.Count == 0) return "empty";
                return string.Join(", ", queue.Select(e =>
                    $"{e.EventType}@tick{e.FireAtTick}({e.SourceCycle})"
                ));
            }
        }
    }
}
