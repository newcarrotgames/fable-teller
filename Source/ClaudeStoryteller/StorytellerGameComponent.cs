using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ClaudeStoryteller
{
    /// <summary>A letter body staged for delivery at the top of the next MakeIntervalIncidents pass.</summary>
    public class PendingLetterEntry : IExposable
    {
        public string Label;
        public string Body;
        public bool IsThreat;
        public bool IsArc;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Label, "label");
            Scribe_Values.Look(ref Body, "body");
            Scribe_Values.Look(ref IsThreat, "isThreat", false);
            Scribe_Values.Look(ref IsArc, "isArc", false);
        }
    }

    public class StorytellerGameComponent : GameComponent
    {
        // Persistent arc log — survives save/load
        private List<ArcLogEntry> arcLog = new List<ArcLogEntry>();

        // Currently active arc tracking
        private string activeArcName;
        private List<string> activeArcEvents = new List<string>();
        private List<string> activeArcOutcomes = new List<string>();
        private int activeArcStartDay;
        private int activeArcTotalEvents; // legacy field, kept for save compat, no longer consulted

        private string activeArcQuestion;
        private string activeArcSummary;
        private string activeArcLastExpectation;
        private string activeArcFaction;
        private List<ArcBeatRecord> activeArcBeats = new List<ArcBeatRecord>();

        private string pendingClosingFlavor;
        private bool arcClosingPending;
        private int pendingClosingTick = -1;
        private int lastArcBeatFiredTick = -1;
        private int emptyContinueCalls;

        // Phase 2: scattered events of these defNames are dropped while this arc is active
        // (they would answer the story_question for it); unresolved threads are memory-only
        // notes carried between arcs, never letter content; factionLedger tracks who has a
        // grudge across arcs. calls_since_beat_authored resets whenever a beat is queued.
        private List<string> arcReservedTypes = new List<string>();
        private List<string> unresolvedThreads = new List<string>();
        private List<FactionLedgerEntry> factionLedger = new List<FactionLedgerEntry>();
        private int callsSinceBeatAuthored;
        private const int MAX_UNRESOLVED_THREADS = 6;

        // Baseline snapshot captured at StartArc, used for now_vs_baseline deltas and
        // ArcLogEntry.ColonistDelta/WealthDelta in FinalizeArc.
        private int baselineColonists;
        private float baselineWealth;
        private int baselineFoodDays;
        private string baselineMedicineTier;
        private int baselineDeaths;
        private int baselineDowned;

        // Persistent unified-call timing (source of truth; StorytellerComp_Claude reads/writes through this)
        private int lastUnifiedCallTick = -1;   // -1 sentinel: "never called"
        private int requestedCallTick = -1;     // -1: no early call requested
        private int nextUnifiedIntervalTicks = -1; // -1 sentinel: not yet set by a response/new game

        // Persistent event history
        private List<string> eventHistoryTypes = new List<string>();
        private List<string> eventHistoryOutcomes = new List<string>();
        private List<string> eventHistoryRequestedTypes = new List<string>();
        private List<string> eventHistorySources = new List<string>();
        private List<int> eventHistoryTicks = new List<int>();

        // Death/downed/threat tracking
        private int lastDeathTick = -999999;
        private int lastDownedTick = -999999;
        private int lastThreatTick = -999999;
        private int colonistDeathsTotal;
        private int colonistDownedTotal;
        private string lastDeathName;
        private string lastDownedName;

        // Phase 3: colonists gained (StoryWatcher_PopAdaptation postfix, best-effort).
        private int colonistJoinedTotal;
        private string lastJoinedName;

        // Phase 3: names since the previous unified call, cleared after each state build
        // (same send-once pattern as PawnEventPatch.uninvitedIncidents).
        private List<string> sinceLastCallJoined = new List<string>();
        private List<string> sinceLastCallDied = new List<string>();
        private List<string> sinceLastCallDowned = new List<string>();

        // Phase 3: letter bodies actually shown for the active arc, for STORY_TRANSCRIPT at
        // FinalizeArc. Persisted, capped, cleared on StartArc/FinalizeArc.
        private List<string> arcLetterBodies = new List<string>();
        private const int MAX_ARC_LETTERS = 12;

        // Disease cooldown tracking
        private int lastDiseaseTick = -999999;

        // Arc completion tracking
        private int lastArcCompletedTick = -999999;

        // Storytelling posture (Claude's current blend)
        private string lastPosture = "";

        // Pending event queue, mirrored to/from the static EventQueue across scribe modes.
        private List<QueuedEvent> pendingQueue = new List<QueuedEvent>();

        // Letters produced mid-pass (letter-only beats, the arc closing letter) are queued here
        // instead of sent immediately, so they cannot steal flavor staged for a sibling letter
        // produced later in the SAME MakeIntervalIncidents pass. Sent at the top of the NEXT pass.
        private List<PendingLetterEntry> pendingLetters = new List<PendingLetterEntry>();

        private const int MAX_EVENT_HISTORY = 30;
        private const int MAX_ARC_LOG = 50;

        public StorytellerGameComponent(Game game)
        {
            // EventQueue is static and outlives a Game. Clear it here so loading or
            // starting a second colony in the same session does not inherit the first
            // one's pending events. ExposeData restores the saved queue afterwards.
            EventQueue.Clear();
            // PawnEventPatch's dedupe statics and uninvited-incident list are static too —
            // same cross-colony leak, same fix.
            PawnEventPatch.ResetSession();
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Collections.Look(ref arcLog, "arcLog", LookMode.Deep);
            Scribe_Values.Look(ref activeArcName, "activeArcName");
            Scribe_Collections.Look(ref activeArcEvents, "activeArcEvents", LookMode.Value);
            Scribe_Collections.Look(ref activeArcOutcomes, "activeArcOutcomes", LookMode.Value);
            Scribe_Values.Look(ref activeArcStartDay, "activeArcStartDay", 0);
            Scribe_Values.Look(ref activeArcTotalEvents, "activeArcTotalEvents", 0);

            Scribe_Values.Look(ref activeArcQuestion, "activeArcQuestion");
            Scribe_Values.Look(ref activeArcSummary, "activeArcSummary");
            Scribe_Values.Look(ref activeArcLastExpectation, "activeArcLastExpectation");
            Scribe_Values.Look(ref activeArcFaction, "activeArcFaction");
            Scribe_Collections.Look(ref activeArcBeats, "activeArcBeats", LookMode.Deep);

            Scribe_Values.Look(ref pendingClosingFlavor, "pendingClosingFlavor");
            Scribe_Values.Look(ref arcClosingPending, "arcClosingPending", false);
            Scribe_Values.Look(ref pendingClosingTick, "pendingClosingTick", -1);
            Scribe_Values.Look(ref lastArcBeatFiredTick, "lastArcBeatFiredTick", -1);
            Scribe_Values.Look(ref emptyContinueCalls, "emptyContinueCalls", 0);
            Scribe_Values.Look(ref callsSinceBeatAuthored, "callsSinceBeatAuthored", 0);
            Scribe_Collections.Look(ref arcReservedTypes, "arcReservedTypes", LookMode.Value);
            Scribe_Collections.Look(ref unresolvedThreads, "unresolvedThreads", LookMode.Value);
            Scribe_Collections.Look(ref factionLedger, "factionLedger", LookMode.Deep);

            Scribe_Values.Look(ref baselineColonists, "baselineColonists", 0);
            Scribe_Values.Look(ref baselineWealth, "baselineWealth", 0f);
            Scribe_Values.Look(ref baselineFoodDays, "baselineFoodDays", 0);
            Scribe_Values.Look(ref baselineMedicineTier, "baselineMedicineTier");
            Scribe_Values.Look(ref baselineDeaths, "baselineDeaths", 0);
            Scribe_Values.Look(ref baselineDowned, "baselineDowned", 0);

            Scribe_Values.Look(ref lastUnifiedCallTick, "lastUnifiedCallTick", -1);
            Scribe_Values.Look(ref requestedCallTick, "requestedCallTick", -1);
            Scribe_Values.Look(ref nextUnifiedIntervalTicks, "nextUnifiedIntervalTicks", -1);

            Scribe_Collections.Look(ref eventHistoryTypes, "eventHistoryTypes", LookMode.Value);
            Scribe_Collections.Look(ref eventHistoryOutcomes, "eventHistoryOutcomes", LookMode.Value);
            Scribe_Collections.Look(ref eventHistoryRequestedTypes, "eventHistoryRequestedTypes", LookMode.Value);
            Scribe_Collections.Look(ref eventHistorySources, "eventHistorySources", LookMode.Value);
            Scribe_Collections.Look(ref eventHistoryTicks, "eventHistoryTicks", LookMode.Value);

            Scribe_Values.Look(ref lastDeathTick, "lastDeathTick", -999999);
            Scribe_Values.Look(ref lastDownedTick, "lastDownedTick", -999999);
            Scribe_Values.Look(ref lastThreatTick, "lastThreatTick", -999999);
            Scribe_Values.Look(ref lastDiseaseTick, "lastDiseaseTick", -999999);
            Scribe_Values.Look(ref lastArcCompletedTick, "lastArcCompletedTick", -999999);
            Scribe_Values.Look(ref lastPosture, "lastPosture", "");

            Scribe_Values.Look(ref colonistDeathsTotal, "colonistDeathsTotal", 0);
            Scribe_Values.Look(ref colonistDownedTotal, "colonistDownedTotal", 0);
            Scribe_Values.Look(ref lastDeathName, "lastDeathName");
            Scribe_Values.Look(ref lastDownedName, "lastDownedName");

            Scribe_Values.Look(ref colonistJoinedTotal, "colonistJoinedTotal", 0);
            Scribe_Values.Look(ref lastJoinedName, "lastJoinedName");
            Scribe_Collections.Look(ref sinceLastCallJoined, "sinceLastCallJoined", LookMode.Value);
            Scribe_Collections.Look(ref sinceLastCallDied, "sinceLastCallDied", LookMode.Value);
            Scribe_Collections.Look(ref sinceLastCallDowned, "sinceLastCallDowned", LookMode.Value);
            Scribe_Collections.Look(ref arcLetterBodies, "arcLetterBodies", LookMode.Value);
            Scribe_Collections.Look(ref pendingTakeTypes, "pendingTakeTypes", LookMode.Value);
            Scribe_Collections.Look(ref pendingLetters, "pendingLetters", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.Saving)
                pendingQueue = EventQueue.PeekAll();

            Scribe_Collections.Look(ref pendingQueue, "pendingQueue", LookMode.Deep);

            // Null safety BEFORE the PostLoadInit block below runs — FinalizeArc("legacy_reset")
            // reads every one of these lists, and on an old save any list added after that save
            // was written comes back null. Guarding after PostLoadInit (as this used to do) is
            // too late: FinalizeArc throws on the null list before the guard ever runs.
            if (arcLog == null) arcLog = new List<ArcLogEntry>();
            if (activeArcBeats == null) activeArcBeats = new List<ArcBeatRecord>();
            if (activeArcEvents == null) activeArcEvents = new List<string>();
            if (activeArcOutcomes == null) activeArcOutcomes = new List<string>();
            if (eventHistoryTypes == null) eventHistoryTypes = new List<string>();
            if (eventHistoryOutcomes == null) eventHistoryOutcomes = new List<string>();
            if (eventHistoryRequestedTypes == null) eventHistoryRequestedTypes = new List<string>();
            if (eventHistorySources == null) eventHistorySources = new List<string>();
            if (eventHistoryTicks == null) eventHistoryTicks = new List<int>();
            if (lastPosture == null) lastPosture = "";
            if (arcReservedTypes == null) arcReservedTypes = new List<string>();
            if (unresolvedThreads == null) unresolvedThreads = new List<string>();
            if (factionLedger == null) factionLedger = new List<FactionLedgerEntry>();
            if (sinceLastCallJoined == null) sinceLastCallJoined = new List<string>();
            if (sinceLastCallDied == null) sinceLastCallDied = new List<string>();
            if (sinceLastCallDowned == null) sinceLastCallDowned = new List<string>();
            if (arcLetterBodies == null) arcLetterBodies = new List<string>();
            if (pendingTakeTypes == null) pendingTakeTypes = new List<string>();
            if (pendingLetters == null) pendingLetters = new List<PendingLetterEntry>();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pendingQueue == null) pendingQueue = new List<QueuedEvent>();
                EventQueue.RestoreAll(pendingQueue);
                ClaudeLogger.LogEntry("QUEUE_RESTORED",
                    $"Restored {EventQueue.Count} pending event(s) from save: {EventQueue.GetQueueSummary()}");

                if (!string.IsNullOrEmpty(activeArcName) && activeArcQuestion == null)
                {
                    // A pre-Phase-1 save (or any arc that somehow lost its question) — this is
                    // the "zombie arc" bug: arc tracking and the queue were never reconciled.
                    // Clear it so the mod does not spend the rest of the game insisting an arc
                    // is active with fields that no longer exist to reconstruct it correctly.
                    FinalizeArc("legacy_reset");
                }
                else if (!string.IsNullOrEmpty(activeArcName))
                {
                    ClaudeLogger.LogEntry("ARC_RECONCILED",
                        $"Arc '{activeArcName}' resumed. Queued beats: {QueuedArcBeats}.");
                }
            }
        }

        // ========== Arc Lifecycle ==========

        /// <summary>
        /// Starts a new arc, capturing a baseline snapshot for later delta reporting.
        /// If an arc is already active it is finalized as "interrupted" first.
        /// </summary>
        public void StartArc(string arcName, string question, Map map)
        {
            if (!string.IsNullOrEmpty(activeArcName))
                FinalizeArc("interrupted");

            activeArcName = arcName;
            activeArcQuestion = question;
            activeArcSummary = null;
            activeArcLastExpectation = null;
            activeArcBeats = new List<ArcBeatRecord>();
            activeArcEvents = new List<string>();
            activeArcOutcomes = new List<string>();
            activeArcStartDay = GenDate.DaysPassed;
            activeArcFaction = null;
            emptyContinueCalls = 0;
            arcClosingPending = false;
            pendingClosingFlavor = null;
            pendingClosingTick = -1;
            lastArcBeatFiredTick = -1;
            arcReservedTypes = new List<string>();
            callsSinceBeatAuthored = 0;
            arcLetterBodies = new List<string>();
            pendingTakeTypes = new List<string>();

            int colonists = map?.mapPawns?.FreeColonists?.Count() ?? 0;
            baselineColonists = colonists;
            baselineWealth = map?.wealthWatcher?.WealthTotal ?? 0f;
            baselineFoodDays = ColonyStateCollector.CurrentFoodDays(map);
            baselineMedicineTier = ColonyStateCollector.MedicineTier(map);
            baselineDeaths = colonistDeathsTotal;
            baselineDowned = colonistDownedTotal;

            ClaudeLogger.LogEntry("ARC_START",
                $"Started arc: {arcName}. Question: {question}. Baseline: {colonists} colonists, " +
                $"wealth {baselineWealth:F0}, food {baselineFoodDays}d, medicine {baselineMedicineTier}.");
        }

        public void SetArcFaction(string factionName) => activeArcFaction = factionName;

        /// <summary>
        /// Records a beat that fired (or failed/fizzled) as part of the active arc. Arc
        /// completion is NEVER count-based any more — this only appends bookkeeping.
        /// </summary>
        public void RecordArcBeat(QueuedEvent q, string resolvedType, string outcome, Map map)
        {
            if (string.IsNullOrEmpty(activeArcName)) return;

            // A beat queued for an arc that has since ended/changed (e.g. FinalizeArc failed to
            // purge every queued entry, or a stale reference survived a reload) must never be
            // recorded against whatever arc happens to be active now.
            if (q != null && !string.IsNullOrEmpty(q.ArcName) && q.ArcName != activeArcName)
            {
                ClaudeLogger.LogEntry("ARC_STRAY_BEAT",
                    $"Ignored beat '{q.EventType}' tagged for arc '{q.ArcName}' while active arc is '{activeArcName}'.");
                return;
            }

            emptyContinueCalls = 0;

            activeArcEvents.Add(resolvedType ?? q?.EventType);
            activeArcOutcomes.Add(outcome);

            var record = new ArcBeatRecord
            {
                Type = resolvedType ?? q?.EventType,
                RequestedType = q?.EventType,
                Outcome = outcome,
                Link = q?.Link,
                CircleStep = q?.CircleStep,
                LinkReason = q?.LinkReason,
                Expect = q?.Expect,
                Day = GenDate.DaysPassed,
                Colonists = map?.mapPawns?.FreeColonists?.Count() ?? 0,
                Wealth = map?.wealthWatcher?.WealthTotal ?? 0f,
                FoodDays = ColonyStateCollector.CurrentFoodDays(map),
                DeathsTotal = colonistDeathsTotal,
                DownedTotal = colonistDownedTotal,
                Faction = q?.Faction
            };
            activeArcBeats.Add(record);
            lastArcBeatFiredTick = Find.TickManager.TicksGame;

            if (!string.IsNullOrEmpty(record.CircleStep) && record.CircleStep == "take")
                RecordTakeType(record.Type);

            UpdateFactionLedger(record, outcome);

            ClaudeLogger.LogEntry("ARC_BEAT_FIRED",
                $"Arc '{activeArcName}' beat: {record.Type} (requested {record.RequestedType}) -> " +
                $"{outcome}. Note: {q?.Note ?? "(none)"}. Day {record.Day}, colonists {record.Colonists}, " +
                $"wealth {record.Wealth:F0}.");
        }

        // Beats with a "take" circle_step whose resolved type, staged for the currently-open
        // arc; flushed into the ArcLogEntry.TakeEventTypes list on FinalizeArc.
        private List<string> pendingTakeTypes = new List<string>();
        private void RecordTakeType(string type)
        {
            if (!string.IsNullOrEmpty(type)) pendingTakeTypes.Add(type);
        }

        /// <summary>
        /// One letter-only ("type": "none") beat per arc: records it as an ArcBeatRecord with
        /// Outcome "letter_only" so timeouts/HasLetterOnlyBeat/BuildArcProgress all see it as a
        /// real beat without going through the event queue at all.
        /// </summary>
        public void RecordLetterOnlyBeat(string circleStep, string link, string linkReason, string expect, string faction, Map map)
        {
            if (string.IsNullOrEmpty(activeArcName)) return;
            emptyContinueCalls = 0;

            var record = new ArcBeatRecord
            {
                Type = "none",
                RequestedType = "none",
                Outcome = "letter_only",
                Link = link,
                CircleStep = circleStep,
                LinkReason = linkReason,
                Expect = expect,
                Day = GenDate.DaysPassed,
                Colonists = map?.mapPawns?.FreeColonists?.Count() ?? 0,
                Wealth = map?.wealthWatcher?.WealthTotal ?? 0f,
                FoodDays = ColonyStateCollector.CurrentFoodDays(map),
                DeathsTotal = colonistDeathsTotal,
                DownedTotal = colonistDownedTotal,
                Faction = faction
            };
            activeArcBeats.Add(record);
            activeArcEvents.Add("none");
            activeArcOutcomes.Add("letter_only");
            lastArcBeatFiredTick = Find.TickManager.TicksGame;

            ClaudeLogger.LogEntry("ARC_BEAT_FIRED",
                $"Arc '{activeArcName}' beat: none (letter_only). Day {record.Day}.");
        }

        public bool HasLetterOnlyBeat() => activeArcBeats.Any(b => b.Outcome == "letter_only");

        /// <summary>All circle steps recorded so far for the active arc, in order fired.</summary>
        public List<string> GetArcBeatSteps() =>
            activeArcBeats.Where(b => !string.IsNullOrEmpty(b.CircleStep)).Select(b => b.CircleStep).ToList();

        /// <summary>
        /// "colonist died or kidnapped since [the previous beat]" -> "bad", else "ok". Compares
        /// the running death/downed counters against the last recorded ArcBeatRecord (the
        /// "previous beat" from the perspective of the beat about to fire).
        /// </summary>
        public string ClassifyPreviousBeatWindow()
        {
            if (activeArcBeats.Count == 0)
                return (colonistDeathsTotal > baselineDeaths || colonistDownedTotal > baselineDowned) ? "bad" : "ok";

            var prev = activeArcBeats[activeArcBeats.Count - 1];
            bool bad = colonistDeathsTotal > prev.DeathsTotal || colonistDownedTotal > prev.DownedTotal;
            return bad ? "bad" : "ok";
        }

        private FactionLedgerEntry GetOrAddLedgerEntry(string factionName)
        {
            var entry = factionLedger.FirstOrDefault(f => f.FactionName == factionName);
            if (entry == null)
            {
                entry = new FactionLedgerEntry { FactionName = factionName };
                factionLedger.Add(entry);
            }
            return entry;
        }

        private void UpdateFactionLedger(ArcBeatRecord record, string outcome)
        {
            if (string.IsNullOrEmpty(record.Faction)) return;

            var entry = GetOrAddLedgerEntry(record.Faction);
            entry.LastContactDay = record.Day;
            entry.LastOutcome = outcome;
            if (ColonyStateCollector.IsThreatEvent(record.Type))
                entry.RaidsSent++;

            int deathsBefore = activeArcBeats.Count > 1
                ? activeArcBeats[activeArcBeats.Count - 2].DeathsTotal
                : baselineDeaths;
            if (record.DeathsTotal > deathsBefore)
                entry.LostPawns += record.DeathsTotal - deathsBefore;
        }

        public List<string> GetRecurringFactions() =>
            factionLedger.Where(f => f.RaidsSent + f.LostPawns > 0 || f.LastContactDay > 0)
                .Select(f => f.FactionName).Distinct().ToList();

        // ========== Arc summary/expectation carried between calls ==========

        public void SetArcSummary(string summary)
        {
            if (!string.IsNullOrEmpty(summary)) activeArcSummary = summary;
        }

        public void SetArcLastExpectation(string expect)
        {
            if (!string.IsNullOrEmpty(expect)) activeArcLastExpectation = expect;
        }

        public void SetArcReservedTypes(List<string> types)
        {
            arcReservedTypes = types ?? new List<string>();
        }

        public List<string> GetArcReservedTypes() => arcReservedTypes ?? new List<string>();

        public void SetUnresolvedThreads(List<string> threads)
        {
            unresolvedThreads = (threads ?? new List<string>()).Take(MAX_UNRESOLVED_THREADS).ToList();
        }

        public List<string> GetUnresolvedThreads() => unresolvedThreads ?? new List<string>();

        public void IncrementCallsSinceBeatAuthored() { callsSinceBeatAuthored++; }
        public void ResetCallsSinceBeatAuthored() { callsSinceBeatAuthored = 0; }

        /// <summary>
        /// Builds the arc_progress block sent to Claude every call while an arc is active.
        /// Returns null when no arc is running (ColonyState.ArcProgress stays null then).
        /// </summary>
        public Models.ArcProgress BuildArcProgress(Map map)
        {
            if (string.IsNullOrEmpty(activeArcName)) return null;

            int daysNow = GenDate.DaysPassed;
            int colonistsNow = map?.mapPawns?.FreeColonists?.Count() ?? 0;
            float wealthNow = map?.wealthWatcher?.WealthTotal ?? 0f;
            int foodDaysNow = ColonyStateCollector.CurrentFoodDays(map);
            string medicineNow = ColonyStateCollector.MedicineTier(map);

            var beatsFired = activeArcBeats.Select((b, i) =>
            {
                var prev = i > 0 ? activeArcBeats[i - 1] : null;
                int prevColonists = prev?.Colonists ?? baselineColonists;
                float prevWealth = prev?.Wealth ?? baselineWealth;
                int prevFood = prev?.FoodDays ?? baselineFoodDays;
                int prevDeaths = prev?.DeathsTotal ?? baselineDeaths;
                int prevDowned = prev?.DownedTotal ?? baselineDowned;
                return new Models.ArcBeatSummary
                {
                    Type = b.Type,
                    RequestedType = b.RequestedType,
                    Outcome = b.Outcome,
                    CircleStep = b.CircleStep,
                    Link = b.Link,
                    Day = b.Day,
                    ChangesAfter = new Models.ArcBeatChanges
                    {
                        Colonists = b.Colonists - prevColonists,
                        Wealth = b.Wealth - prevWealth,
                        FoodDays = b.FoodDays - prevFood,
                        Deaths = b.DeathsTotal - prevDeaths,
                        Downed = b.DownedTotal - prevDowned
                    }
                };
            }).ToList();

            var lastBeat = activeArcBeats.Count > 0 ? activeArcBeats[activeArcBeats.Count - 1] : null;

            var sinceLastBeat = new Models.ArcDeltas
            {
                Days = lastBeat != null
                    ? (Find.TickManager.TicksGame - lastArcBeatFiredTick) / GenDate.TicksPerDay
                    : daysNow - activeArcStartDay,
                Colonists = colonistsNow - (lastBeat?.Colonists ?? baselineColonists),
                Wealth = wealthNow - (lastBeat?.Wealth ?? baselineWealth),
                FoodDays = foodDaysNow - (lastBeat?.FoodDays ?? baselineFoodDays),
                Deaths = colonistDeathsTotal - (lastBeat?.DeathsTotal ?? baselineDeaths),
                Downed = colonistDownedTotal - (lastBeat?.DownedTotal ?? baselineDowned),
                MedicineNow = medicineNow
            };

            var nowVsBaseline = new Models.ArcDeltas
            {
                Days = daysNow - activeArcStartDay,
                Colonists = colonistsNow - baselineColonists,
                Wealth = wealthNow - baselineWealth,
                FoodDays = foodDaysNow - baselineFoodDays,
                Deaths = colonistDeathsTotal - baselineDeaths,
                Downed = colonistDownedTotal - baselineDowned,
                MedicineNow = medicineNow
            };

            var queuedBeats = EventQueue.PeekNarrativeFor(activeArcName).Select(q => new Models.QueuedBeatSummary
            {
                Type = q.EventType,
                CircleStep = q.CircleStep,
                Link = q.Link,
                FiresInHours = (q.FireAtTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour,
                FireWhen = q.FireWhen
            }).ToList();

            return new Models.ArcProgress
            {
                Name = activeArcName,
                StoryQuestion = activeArcQuestion,
                ArcFaction = activeArcFaction,
                DayStarted = activeArcStartDay,
                DaysActive = daysNow - activeArcStartDay,
                SummarySoFar = activeArcSummary,
                LastExpectation = activeArcLastExpectation,
                Baseline = new Models.ArcBaseline
                {
                    Colonists = baselineColonists,
                    Wealth = baselineWealth,
                    FoodDays = baselineFoodDays,
                    Medicine = baselineMedicineTier
                },
                NowVsBaseline = nowVsBaseline,
                BeatsFired = beatsFired,
                SinceLastBeat = sinceLastBeat,
                QueuedBeats = queuedBeats,
                StepsUsed = GetArcBeatSteps(),
                CallsSinceBeatAuthored = callsSinceBeatAuthored,
                ClosingPending = arcClosingPending
            };
        }

        public void NoteEmptyContinue() { emptyContinueCalls++; }

        /// <summary>Marks the active arc for a deferred closing letter once its final beat (if any) fires.</summary>
        public void MarkArcClosing(string closingFlavor)
        {
            if (string.IsNullOrEmpty(activeArcName)) return;
            arcClosingPending = true;
            pendingClosingFlavor = closingFlavor;
            pendingClosingTick = -1; // not due yet; ScheduleClosingLetterIfDone sets this once ready
            ClaudeLogger.LogEntry("ARC_CLOSING",
                $"Arc '{activeArcName}' marked for closing. Final beats queued: {QueuedArcBeats}.");
        }

        /// <summary>
        /// Call every MakeIntervalIncidents pass, after PopReady. If closing is pending and no
        /// more narrative beats are queued for this arc, stage the letter for delivery NEXT
        /// interval — not this one, or it races the just-fired beat's own staged flavor.
        /// </summary>
        public void ScheduleClosingLetterIfDone()
        {
            if (!arcClosingPending || pendingClosingTick >= 0) return;
            if (QueuedArcBeats > 0) return; // still waiting on the final beat to fire
            pendingClosingTick = Find.TickManager.TicksGame + 1000;
        }

        /// <summary>Returns the closing flavor if it is due this interval, else null. Clears pending state.</summary>
        public string TryTakeDueClosingLetter()
        {
            if (!arcClosingPending || pendingClosingTick < 0) return null;
            if (Find.TickManager.TicksGame < pendingClosingTick) return null;

            string flavor = pendingClosingFlavor;
            arcClosingPending = false;
            pendingClosingFlavor = null;
            pendingClosingTick = -1;
            return flavor;
        }

        public void CheckArcTimeouts(int maxArcDays, int arcStallDays)
        {
            if (string.IsNullOrEmpty(activeArcName)) return;

            int days = GenDate.DaysPassed;
            int daysSinceStart = days - activeArcStartDay;
            int daysSinceLastBeat = lastArcBeatFiredTick < 0
                ? daysSinceStart
                : (Find.TickManager.TicksGame - lastArcBeatFiredTick) / GenDate.TicksPerDay;

            if (arcClosingPending)
            {
                // end_arc already decided this arc is ending on purpose — never let a timeout
                // reclassify that as timed_out/stalled/abandoned. Either let the normal closing
                // pipeline finish it as "completed" once the last beat clears the queue, or, past
                // a hard deadline, force the queue clear so that pipeline can proceed anyway.
                if (QueuedArcBeats == 0)
                {
                    ScheduleClosingLetterIfDone();
                    return;
                }
                if (daysSinceStart >= maxArcDays + 3)
                {
                    EventQueue.ClearNarrativeFor(activeArcName);
                    ClaudeLogger.LogEntry("ARC_CLOSING_FORCED",
                        $"Arc '{activeArcName}' past hard deadline ({daysSinceStart}d) with beats still queued; " +
                        "cleared so the pending closing flavor can be delivered.");
                }
                return;
            }

            if (daysSinceStart >= maxArcDays)
            {
                FinalizeArc("timed_out");
                return;
            }
            if (QueuedArcBeats == 0 && daysSinceLastBeat >= arcStallDays)
            {
                FinalizeArc("stalled");
                return;
            }
            if (emptyContinueCalls >= 3)
            {
                FinalizeArc("abandoned");
            }
        }

        public void RequestEarlyCall(string reason)
        {
            requestedCallTick = Find.TickManager.TicksGame;
            ClaudeLogger.LogEntry("EARLY_CALL_REQUESTED", $"Reason: {reason}");
        }

        /// <summary>
        /// Finalize the current arc and add it to the persistent log.
        /// outcome: completed | interrupted | timed_out | stalled | abandoned | legacy_reset
        /// </summary>
        public void FinalizeArc(string outcome)
        {
            if (string.IsNullOrEmpty(activeArcName)) return;

            // Whatever the outcome, any beat still queued for this arc must never fire later
            // under a different (or no) active arc — purge it now.
            EventQueue.ClearNarrativeFor(activeArcName);

            int colonistsNow = PawnsFinder.AllMaps_FreeColonists.Count();
            float wealthNow = Find.CurrentMap?.wealthWatcher?.WealthTotal ?? 0f;

            var entry = new ArcLogEntry
            {
                ArcName = activeArcName,
                Events = new List<string>(activeArcEvents),
                EventOutcomes = new List<string>(activeArcOutcomes),
                Outcome = outcome,
                StartDay = activeArcStartDay,
                EndDay = GenDate.DaysPassed,
                ColonistCount = colonistsNow,
                Wealth = wealthNow,
                Phase = ColonyStateCollector.GetCurrentPhase(),
                NarrativeState = ColonyStateCollector.GetCurrentNarrativeState(),
                StoryQuestion = activeArcQuestion,
                Summary = activeArcSummary,
                Links = activeArcBeats.Where(b => b.Link != null).Select(b => b.Link).ToList(),
                Steps = activeArcBeats.Where(b => b.CircleStep != null).Select(b => b.CircleStep).ToList(),
                Deaths = colonistDeathsTotal - baselineDeaths,
                ColonistDelta = colonistsNow - baselineColonists,
                WealthDelta = wealthNow - baselineWealth,
                Faction = activeArcFaction,
                UnresolvedThreads = new List<string>(unresolvedThreads ?? new List<string>()),
                TakeEventTypes = new List<string>(pendingTakeTypes)
            };

            arcLog.Add(entry);
            while (arcLog.Count > MAX_ARC_LOG) arcLog.RemoveAt(0);

            if (!string.IsNullOrEmpty(activeArcFaction))
            {
                var ledgerEntry = GetOrAddLedgerEntry(activeArcFaction);
                ledgerEntry.LastOutcome = outcome;
            }

            lastArcCompletedTick = Find.TickManager.TicksGame;

            ClaudeLogger.LogEntry("ARC_COMPLETE",
                $"Arc '{activeArcName}' {outcome}. Question: {activeArcQuestion}. " +
                $"Beats: {string.Join(", ", activeArcEvents)}. Deaths: {entry.Deaths}. " +
                $"ColonistDelta: {entry.ColonistDelta}. WealthDelta: {entry.WealthDelta:F0}. " +
                $"Total arcs logged: {arcLog.Count}.");

            // Parker/Stone audit block: the question, every beat's link/step/outcome, and the
            // actual letter bodies shown for this arc — one grep instead of stitching several
            // log tags back together by hand.
            var transcript = new List<string> { $"Story question: {activeArcQuestion}" };
            foreach (var b in activeArcBeats)
            {
                transcript.Add($"Day {b.Day} | step={b.CircleStep ?? "-"} | link={b.Link ?? "-"} | " +
                    $"type={b.Type ?? "-"} (requested {b.RequestedType ?? "-"}) | outcome={b.Outcome} | " +
                    $"link_reason={b.LinkReason ?? "-"} | expect={b.Expect ?? "-"}");
            }
            transcript.Add("Letters shown:");
            foreach (var letter in arcLetterBodies) transcript.Add("- " + letter);
            ClaudeLogger.LogEntry("STORY_TRANSCRIPT", string.Join("\n", transcript));

            activeArcName = null;
            activeArcQuestion = null;
            activeArcSummary = null;
            activeArcLastExpectation = null;
            activeArcEvents = new List<string>();
            activeArcOutcomes = new List<string>();
            activeArcBeats = new List<ArcBeatRecord>();
            activeArcTotalEvents = 0;
            activeArcFaction = null;
            arcClosingPending = false;
            pendingClosingFlavor = null;
            pendingClosingTick = -1;
            emptyContinueCalls = 0;
            lastArcBeatFiredTick = -1;
            arcReservedTypes = new List<string>();
            pendingTakeTypes = new List<string>();
            callsSinceBeatAuthored = 0;
            arcLetterBodies = new List<string>();
        }

        // ========== Event History ==========

        public void RecordEvent(string type, string outcome, string requestedType, string source)
        {
            eventHistoryTypes.Insert(0, type);
            eventHistoryOutcomes.Insert(0, outcome);
            eventHistoryRequestedTypes.Insert(0, requestedType);
            eventHistorySources.Insert(0, source);
            eventHistoryTicks.Insert(0, Find.TickManager.TicksGame);

            while (eventHistoryTypes.Count > MAX_EVENT_HISTORY)
            {
                eventHistoryTypes.RemoveAt(eventHistoryTypes.Count - 1);
                eventHistoryOutcomes.RemoveAt(eventHistoryOutcomes.Count - 1);
                eventHistoryRequestedTypes.RemoveAt(eventHistoryRequestedTypes.Count - 1);
                eventHistorySources.RemoveAt(eventHistorySources.Count - 1);
                eventHistoryTicks.RemoveAt(eventHistoryTicks.Count - 1);
            }

            if (ColonyStateCollector.IsThreatEvent(type))
                lastThreatTick = Find.TickManager.TicksGame;
        }

        public void RecordColonistDeath(string name)
        {
            lastDeathTick = Find.TickManager.TicksGame;
            colonistDeathsTotal++;
            lastDeathName = name;
            sinceLastCallDied.Add(name);
            ClaudeLogger.LogEntry("DEATH_RECORDED", $"{name} died. Total deaths this game: {colonistDeathsTotal}.");
        }

        public void RecordColonistDowned(string name)
        {
            lastDownedTick = Find.TickManager.TicksGame;
            colonistDownedTotal++;
            lastDownedName = name;
            sinceLastCallDowned.Add(name);
            ClaudeLogger.LogEntry("DOWNED_RECORDED", $"{name} was downed. Total downed this game: {colonistDownedTotal}.");
        }

        /// <summary>Best-effort: StoryWatcher_PopAdaptation.Notify_PawnEvent(pawn, GainedColonist) postfix.</summary>
        public void RecordColonistJoined(string name)
        {
            colonistJoinedTotal++;
            lastJoinedName = name;
            sinceLastCallJoined.Add(name);
            ClaudeLogger.LogEntry("COLONIST_JOINED", $"{name} joined. Total joined this game: {colonistJoinedTotal}.");
        }

        public int ColonistJoinedTotal => colonistJoinedTotal;
        public string LastJoinedName => lastJoinedName;

        /// <summary>"joined: X; died: Y; downed: Z" since the previous unified call, or null.</summary>
        public string GetCastChangesSinceLastCall()
        {
            var parts = new List<string>();
            if (sinceLastCallJoined.Count > 0) parts.Add("joined: " + string.Join(", ", sinceLastCallJoined));
            if (sinceLastCallDied.Count > 0) parts.Add("died: " + string.Join(", ", sinceLastCallDied));
            if (sinceLastCallDowned.Count > 0) parts.Add("downed: " + string.Join(", ", sinceLastCallDowned));
            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }

        /// <summary>Send-once semantics: called after a state build has consumed the changes.</summary>
        public void ClearCastChangesSinceLastCall()
        {
            sinceLastCallJoined.Clear();
            sinceLastCallDied.Clear();
            sinceLastCallDowned.Clear();
        }

        /// <summary>
        /// One letter body actually shown for the active arc (opening/beat/closing/letter-only),
        /// for the STORY_TRANSCRIPT block logged at FinalizeArc. No-op with no active arc.
        /// </summary>
        public void RecordArcLetter(string body)
        {
            if (string.IsNullOrEmpty(activeArcName) || string.IsNullOrEmpty(body)) return;
            arcLetterBodies.Add(body);
            while (arcLetterBodies.Count > MAX_ARC_LETTERS) arcLetterBodies.RemoveAt(0);
        }

        // ========== Disease Tracking ==========

        public void RecordDiseaseFired()
        {
            lastDiseaseTick = Find.TickManager.TicksGame;
            ClaudeLogger.LogEntry("DISEASE_COOLDOWN", $"Disease fired. Next disease blocked until day {GenDate.DaysPassed + 25}+");
        }

        public int DaysSinceLastDisease
        {
            get
            {
                if (lastDiseaseTick < 0) return 999;
                return (Find.TickManager.TicksGame - lastDiseaseTick) / GenDate.TicksPerDay;
            }
        }

        // ========== Arc Completion Tracking ==========

        public int DaysSinceArcCompleted
        {
            get
            {
                if (lastArcCompletedTick < 0) return 999;
                return (Find.TickManager.TicksGame - lastArcCompletedTick) / GenDate.TicksPerDay;
            }
        }

        public int QueuedArcBeats =>
            string.IsNullOrEmpty(activeArcName) ? 0 : EventQueue.CountNarrativeFor(activeArcName);

        // ========== Posture ==========

        public string LastPosture
        {
            get => lastPosture ?? "";
            set => lastPosture = value ?? "";
        }

        // ========== Unified call timing ==========

        public int LastUnifiedCallTick => lastUnifiedCallTick;
        public int RequestedCallTick => requestedCallTick;
        public void SetLastUnifiedCallTick(int t) => lastUnifiedCallTick = t;
        public void ClearRequestedCallTick() => requestedCallTick = -1;

        // -1 sentinel: no interval has been persisted yet (brand new game, or an old save
        // written before this field existed). StorytellerComp_Claude falls back to the default
        // first-call interval in that case rather than treating -1 as a real tick count.
        public int NextUnifiedIntervalTicks => nextUnifiedIntervalTicks;
        public void SetNextUnifiedIntervalTicks(int ticks) => nextUnifiedIntervalTicks = ticks;

        // ========== Pending letters (deferred delivery) ==========

        /// <summary>Stages a letter for delivery at the top of the NEXT MakeIntervalIncidents pass.</summary>
        public void QueuePendingLetter(string label, string body, bool isThreat, bool isArc)
        {
            if (string.IsNullOrEmpty(body)) return;
            pendingLetters.Add(new PendingLetterEntry { Label = label, Body = body, IsThreat = isThreat, IsArc = isArc });
        }

        /// <summary>Returns and clears every letter staged by the previous pass.</summary>
        public List<PendingLetterEntry> TakePendingLetters()
        {
            var result = pendingLetters;
            pendingLetters = new List<PendingLetterEntry>();
            return result;
        }

        // ========== Accessors ==========

        public List<ArcLogEntry> GetArcLog() => arcLog;
        public int ArcCount => arcLog.Count;
        public string ActiveArcName => activeArcName;
        public string ActiveArcQuestion => activeArcQuestion;
        public string ActiveArcFaction => activeArcFaction;
        public int GetActiveArcStartDay() => activeArcStartDay;
        public int LastDeathTick => lastDeathTick;
        public int LastDownedTick => lastDownedTick;
        public int LastThreatTick => lastThreatTick;
        public int ColonistDeathsTotal => colonistDeathsTotal;
        public int ColonistDownedTotal => colonistDownedTotal;
        public string LastDeathName => lastDeathName;
        public string LastDownedName => lastDownedName;

        public List<Models.PastEvent> GetRecentEvents(int count)
        {
            var result = new List<Models.PastEvent>();
            int currentTick = Find.TickManager.TicksGame;

            for (int i = 0; i < count && i < eventHistoryTypes.Count; i++)
            {
                result.Add(new Models.PastEvent
                {
                    Type = eventHistoryTypes[i],
                    DaysAgo = (currentTick - eventHistoryTicks[i]) / GenDate.TicksPerDay,
                    Outcome = eventHistoryOutcomes[i],
                    RequestedType = i < eventHistoryRequestedTypes.Count ? eventHistoryRequestedTypes[i] : null,
                    Source = i < eventHistorySources.Count ? eventHistorySources[i] : null
                });
            }

            return result;
        }

        public List<string> GetRecentEventTypes(int count)
        {
            return eventHistoryTypes.Take(count).ToList();
        }

        // ========== Static Accessor ==========

        public static StorytellerGameComponent Get()
        {
            return Current.Game?.GetComponent<StorytellerGameComponent>();
        }
    }
}
