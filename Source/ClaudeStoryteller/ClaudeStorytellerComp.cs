using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using ClaudeStoryteller.Models;

namespace ClaudeStoryteller
{
    public class StorytellerCompProperties_Claude : StorytellerCompProperties
    {
        public StorytellerCompProperties_Claude()
        {
            compClass = typeof(StorytellerComp_Claude);
        }
    }

    public class StorytellerComp_Claude : StorytellerComp
    {
        // Unified call timer
        private int lastUnifiedCallTick = 0;
        private int nextUnifiedIntervalTicks = 0;
        private bool unifiedCallInProgress = false;

        // Legacy timers kept for the settings sliders — Claude adjusts these via response
        private int lastMinorTick = 0;
        private int lastMajorTick = 0;
        private int lastNarrativeTick = 0;

        private static readonly object lockObj = new object();
        private bool initialized = false;

        private UnifiedResponse pendingUnifiedResponse = null;

        private DifficultyInfo cachedDifficulty = null;
        private int lastDifficultyCheckTick = 0;
        private const int DIFFICULTY_CHECK_INTERVAL = 2500;

        // ========== Call Timing ==========
        // Bounds live in mod settings so the player can tune pacing and API spend.
        private static float UnifiedCallMinDays => ClaudeStorytellerMod.settings.CallMinDays;
        private static float UnifiedCallMaxDays => ClaudeStorytellerMod.settings.CallMaxDays;

        // Minimum hours between events within a narrative arc (just enough to not fire same tick)
        private const float ARC_EVENT_MIN_SPACING_HOURS = 2f;

        // Disease hard cooldown in days (enforced in ColonyStateCollector + here as safety net)
        private const int DISEASE_COOLDOWN_DAYS = 25;

        private static readonly HashSet<string> ThreatEvents = new HashSet<string>
        {
            "RaidEnemy", "Infestation", "MechCluster", "ManhunterPack",
            "Disease_Plague", "Disease_Flu", "Disease_Malaria", "Disease_GutWorms",
            "ToxicFallout", "PsychicDrone", "DefoliatorShipPartCrash",
            "ColdSnap", "HeatWave", "Flashstorm",
            // Core
            "Ambush", "ManhunterAmbush", "AnimalInsanityMass", "AnimalInsanitySingle",
            "DeepDrillInfestation", "CaravanDemand", "RansomDemand",
            "PsychicEmanatorShipPartCrash", "VolcanicWinter",
            // Royalty
            "ProblemCauser", "Disease_Abasia", "Disease_BloodRot",
            // Biotech / Odyssey
            "NoxiousHaze", "VolcanicAsh", "Drought", "GillRot",
            // Core diseases
            "Disease_SleepingSickness", "Disease_OrganDecay",
            "Disease_FibrousMechanites", "Disease_SensoryMechanites", "Disease_MuscleParasites",
            // Anomaly
            "ChimeraAssault", "DevourerAssault", "GorehulkAssault", "FleshbeastAttack",
            "ShamblerSwarm", "SmallShamblerSwarm", "ShamblerAssault", "GhoulAttack",
            "SightstealerSwarm", "SightstealerArrival", "FrenziedAnimals",
            "BloodRain", "DeathPall", "UnnaturalDarkness"
        };

        private static readonly HashSet<string> MajorThreatEvents = new HashSet<string>
        {
            "RaidEnemy", "Infestation", "MechCluster", "DefoliatorShipPartCrash",
            // Core
            "Ambush", "ManhunterAmbush", "DeepDrillInfestation",
            // Royalty
            "ProblemCauser",
            // Anomaly
            "ShamblerAssault", "SightstealerArrival", "FleshbeastAttack",
            "ChimeraAssault", "DevourerAssault", "GorehulkAssault", "UnnaturalDarkness"
        };

        private static readonly HashSet<string> DiseaseEvents = new HashSet<string>
        {
            "Disease_Plague", "Disease_Flu", "Disease_Malaria", "Disease_GutWorms",
            "Disease_FibrousMechanites", "Disease_SensoryMechanites", "Disease_MuscleParasites",
            "Disease_SleepingSickness", "Disease_OrganDecay", "Disease_AnimalFlu", "Disease_AnimalPlague",
            "Disease_Abasia", "Disease_BloodRot", "GillRot"
        };

        // Fallback events when Claude's choice can't fire
        private static readonly List<string> MinorFallbacks = new List<string>
        {
            "ShipChunkDrop", "ResourcePodCrash", "WandererJoin", "TraderCaravanArrival",
            "VisitorGroup", "TravelerGroup", "OrbitalTraderArrival", "SelfTame"
        };

        private static readonly List<string> MajorFallbacks = new List<string>
        {
            "RaidEnemy", "TraderCaravanArrival", "ResourcePodCrash", "RefugeePodCrash",
            "WandererJoin", "TravelerGroup"
        };

        private static readonly Dictionary<string, string> EventNameMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Disease mappings
            {"Disease", "Disease_Plague"},
            {"Plague", "Disease_Plague"},
            {"Disease_Plague", "Disease_Plague"},
            {"Flu", "Disease_Flu"},
            {"Disease_Flu", "Disease_Flu"},
            {"Malaria", "Disease_Malaria"},
            {"Disease_Malaria", "Disease_Malaria"},
            {"GutWorms", "Disease_GutWorms"},
            {"Disease_GutWorms", "Disease_GutWorms"},
            {"MuscleParasites", "Disease_MuscleParasites"},
            {"Disease_MuscleParasites", "Disease_MuscleParasites"},
            {"FibrousMechanites", "Disease_FibrousMechanites"},
            {"Disease_FibrousMechanites", "Disease_FibrousMechanites"},
            {"SensoryMechanites", "Disease_SensoryMechanites"},
            {"Disease_SensoryMechanites", "Disease_SensoryMechanites"},

            // Raid mappings
            {"Raid", "RaidEnemy"},
            {"EnemyRaid", "RaidEnemy"},
            {"RaidEnemy", "RaidEnemy"},

            // Weather/Environment
            {"ToxicFallout", "ToxicFallout"},
            {"VolcanicWinter", "VolcanicWinter"},
            {"ColdSnap", "ColdSnap"},
            {"HeatWave", "HeatWave"},
            {"Flashstorm", "Flashstorm"},
            {"Eclipse", "Eclipse"},
            {"SolarFlare", "SolarFlare"},
            {"Aurora", "Aurora"},

            // Infestations
            {"Infestation", "Infestation"},
            {"DeepDrillInfestation", "DeepDrillInfestation"},

            // Positive events
            {"CargoDropPod", "ResourcePodCrash"},
            {"ResourcePod", "ResourcePodCrash"},
            {"ResourcePodCrash", "ResourcePodCrash"},
            {"ShipChunkDrop", "ShipChunkDrop"},
            {"WandererJoin", "WandererJoin"},
            {"WandererJoins", "WandererJoin"},
            {"Wanderer", "WandererJoin"},
            {"TraderArrival", "TraderCaravanArrival"},
            {"TraderCaravan", "TraderCaravanArrival"},
            {"TraderCaravanArrival", "TraderCaravanArrival"},
            {"Trader", "TraderCaravanArrival"},
            {"VisitorGroup", "VisitorGroup"},
            {"Visitors", "VisitorGroup"},
            {"TravelerGroup", "TravelerGroup"},
            {"Traveler", "TravelerGroup"},
            {"OrbitalTraderArrival", "OrbitalTraderArrival"},
            {"OrbitalTrader", "OrbitalTraderArrival"},

            // Animals
            {"ManhunterPack", "ManhunterPack"},
            {"Manhunter", "ManhunterPack"},
            {"ManhunterAmbush", "ManhunterPack"},
            {"AnimalInsanity", "AnimalInsanityMass"},
            {"AnimalInsanityMass", "AnimalInsanityMass"},
            {"AnimalInsanitySingle", "AnimalInsanitySingle"},
            {"HerdMigration", "HerdMigration"},
            {"Herd", "HerdMigration"},
            {"FarmAnimalsWanderIn", "FarmAnimalsWanderIn"},
            {"FarmAnimals", "FarmAnimalsWanderIn"},
            {"ThrumboPasses", "ThrumboPasses"},
            {"Thrumbo", "ThrumboPasses"},
            {"WildManWandersIn", "WildManWandersIn"},
            {"WildMan", "WildManWandersIn"},
            {"SelfTame", "SelfTame"},

            // Mechs
            {"MechCluster", "MechCluster"},
            {"MechanoidCluster", "MechCluster"},
            {"Mechanoid", "MechCluster"},

            // Ship parts
            {"Defoliator", "DefoliatorShipPartCrash"},
            {"DefoliatorShipPartCrash", "DefoliatorShipPartCrash"},
            {"DefoliatorShip", "DefoliatorShipPartCrash"},
            {"PsychicShip", "PsychicEmanatorShipPartCrash"},
            {"PsychicEmanator", "PsychicEmanatorShipPartCrash"},
            {"PsychicEmanatorShipPartCrash", "PsychicEmanatorShipPartCrash"},

            // Psychic
            {"PsychicDrone", "PsychicDrone"},
            {"PsychicSoothe", "PsychicSoothe"},

            // Misc threats
            {"ShortCircuit", "ShortCircuit"},
            {"CropBlight", "CropBlight"},
            {"Blight", "CropBlight"},
            {"Alphabeavers", "Alphabeavers"},
            {"Beavers", "Alphabeavers"},

            // Refugees/Pods
            {"RefugeePodCrash", "RefugeePodCrash"},
            {"RefugeePod", "RefugeePodCrash"},
            {"Refugee", "RefugeePodCrash"},
            {"TransportPodCrash", "RefugeePodCrash"},
            {"EscapeShuttleCrash", "RefugeePodCrash"},

            // Quests
            {"Quest", "GiveQuest_Random"},
            {"QuestOffer", "GiveQuest_Random"},
            {"GiveQuest", "GiveQuest_Random"},

            // Party/Social
            {"Party", "Party"},
            {"Wedding", "Wedding"},
        };

        private static string ResolveEventName(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;

            if (EventNameMapping.TryGetValue(type, out string mapped))
                return mapped;

            return type;
        }

        // The three sets above are the curated vanilla/DLC classification. Mod-added
        // incidents are classified at runtime from their IncidentDef category, so every
        // membership test has to consult both. Missing this would let a mod raid slip past
        // the difficulty gate and past threat spacing.

        private static bool IsThreat(string resolvedType)
        {
            return ThreatEvents.Contains(resolvedType)
                || ColonyStateCollector.IsThreatEvent(resolvedType);
        }

        private static bool IsMajorThreat(string resolvedType)
        {
            return MajorThreatEvents.Contains(resolvedType)
                || ColonyStateCollector.IsMajorThreatEvent(resolvedType);
        }

        private static bool IsDisease(string resolvedType)
        {
            return DiseaseEvents.Contains(resolvedType)
                || ColonyStateCollector.IsDiseaseEvent(resolvedType);
        }

        private DifficultyInfo GetDifficulty()
        {
            int currentTick = Find.TickManager.TicksGame;
            if (cachedDifficulty == null || currentTick - lastDifficultyCheckTick > DIFFICULTY_CHECK_INTERVAL)
            {
                cachedDifficulty = ColonyStateCollector.CollectDifficulty();
                lastDifficultyCheckTick = currentTick;
            }
            return cachedDifficulty;
        }

        private void InitializeTimers(int currentTick)
        {
            ClaudeLogger.Initialize();

            // `initialized` is a transient comp field — it is false after EVERY load, not just a
            // brand new game, because StorytellerComp instances are recreated on load. The old
            // code treated every "not initialized yet" as a new game and stomped the persisted
            // timer with SetLastUnifiedCallTick(currentTick) + the 3-day default, silently
            // discarding however long was actually left before the next call and whatever
            // interval Claude had last chosen via next_call_days. Only seed fresh state when the
            // GameComponent's LastUnifiedCallTick is still the -1 "never called" sentinel — a
            // truly new game. Otherwise restore both values from the component.
            var comp = StorytellerGameComponent.Get();
            int persistedLastCall = comp?.LastUnifiedCallTick ?? -1;

            if (persistedLastCall < 0)
            {
                lastUnifiedCallTick = currentTick;
                comp?.SetLastUnifiedCallTick(currentTick);

                // Default: first unified call after 3 game days, clamped to the configured range
                float firstCallDays = ClaudeStorytellerMod.settings.ClampCallDays(3f);
                nextUnifiedIntervalTicks = (int)(firstCallDays * GenDate.TicksPerDay);
                comp?.SetNextUnifiedIntervalTicks(nextUnifiedIntervalTicks);

                ClaudeLogger.LogEntry("SCHEDULER",
                    $"Unified call system initialized (new game). First call in ~{firstCallDays:F1} game days " +
                    $"(range {UnifiedCallMinDays:F1}-{UnifiedCallMaxDays:F1}).");
            }
            else
            {
                lastUnifiedCallTick = persistedLastCall;
                int persistedInterval = comp?.NextUnifiedIntervalTicks ?? -1;
                if (persistedInterval > 0)
                {
                    nextUnifiedIntervalTicks = persistedInterval;
                }
                else
                {
                    // Old save from before nextUnifiedIntervalTicks was persisted — fall back to
                    // the same default a new game gets, rather than firing immediately.
                    float fallbackDays = ClaudeStorytellerMod.settings.ClampCallDays(3f);
                    nextUnifiedIntervalTicks = (int)(fallbackDays * GenDate.TicksPerDay);
                }

                ClaudeLogger.LogEntry("SCHEDULER",
                    $"Unified call system restored from save. Last call tick {lastUnifiedCallTick}, " +
                    $"next interval {nextUnifiedIntervalTicks / (float)GenDate.TicksPerDay:F1} game days.");
            }

            lastMinorTick = currentTick;
            lastMajorTick = currentTick;
            lastNarrativeTick = currentTick;

            // Clear any stale state from previous game
            pendingUnifiedResponse = null;
            unifiedCallInProgress = false;
            cachedDifficulty = null;
            lastDifficultyCheckTick = 0;

            initialized = true;
        }

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!ClaudeStorytellerMod.settings.enabled)
                yield break;

            if (!ClaudeStorytellerMod.settings.HasApiKey)
                yield break;

            Map map = target as Map;
            if (map == null)
                yield break;

            int currentTick = Find.TickManager.TicksGame;

            // Detect new game or game load — tick reset means fresh state needed
            if (!initialized || currentTick < lastUnifiedCallTick)
            {
                InitializeTimers(currentTick);
            }

            // ========== Process pending unified response ==========
            UnifiedResponse unifiedResp = null;
            lock (lockObj)
            {
                if (pendingUnifiedResponse != null)
                {
                    unifiedResp = pendingUnifiedResponse;
                    pendingUnifiedResponse = null;
                }
            }

            if (unifiedResp != null)
            {
                foreach (var incident in ProcessUnifiedResponse(unifiedResp, target))
                    yield return incident;
            }

            // ========== Fire queued events ==========
            var arcComp = StorytellerGameComponent.Get();

            // If staged flavor was never absorbed by a vanilla letter, send it alone.
            string orphanedFlavor = LetterFlavorPatch.TakeExpiredFlavor(
                out bool orphanedIsArc, out string orphanedLabel, out bool orphanedIsThreat);
            if (!string.IsNullOrEmpty(orphanedFlavor))
            {
                SendNarrativeLetter(
                    orphanedLabel ?? "The world turns",
                    orphanedFlavor,
                    orphanedIsThreat ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent);
                if (orphanedIsArc)
                    arcComp?.RecordArcLetter(orphanedFlavor);
            }

            // ========== Send letters queued by the PREVIOUS pass ==========
            // Letter-only beats and the arc closing letter are staged on the GameComponent rather
            // than sent synchronously mid-pass. Sending them here — after the expired-flavor
            // flush above, before any new events pop below — means a letter produced later in
            // THIS pass can never race them for the (now-FIFO) staged-flavor queue.
            var duePendingLetters = arcComp?.TakePendingLetters();
            if (duePendingLetters != null)
            {
                foreach (var letter in duePendingLetters)
                {
                    SendNarrativeLetter(letter.Label, letter.Body,
                        letter.IsThreat ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent);
                }
            }

            // ========== Arc closing letter / timeouts ==========
            // Runs before PopReady so a due closing letter and a timed-out arc are settled
            // before this pass considers firing more beats from a (possibly just-finalized) arc.
            arcComp?.ScheduleClosingLetterIfDone();
            arcComp?.CheckArcTimeouts(
                (int)ClaudeStorytellerMod.settings.maxArcDays,
                (int)ClaudeStorytellerMod.settings.arcStallDays);

            string dueClosing = arcComp?.TryTakeDueClosingLetter();
            if (!string.IsNullOrEmpty(dueClosing))
            {
                string filledClosing = ApplySlotTokens(dueClosing, arcComp);
                string closingLabel = arcComp.ActiveArcName ?? "The story ends";
                arcComp.RecordArcLetter(filledClosing);
                // Queued, not sent — delivered at the top of the NEXT pass (see above), so it
                // cannot clobber flavor staged for a beat that fires later in THIS pass.
                arcComp.QueuePendingLetter(closingLabel, filledClosing, false, false);
                ClaudeLogger.LogEntry("CLOSING_LETTER_QUEUED", $"Arc closing letter queued for next pass: {filledClosing}");
                arcComp.FinalizeArc("completed");
            }

            // ========== Fire queued events ==========
            // Eager-pop/lazy-yield hazard: MakeIntervalIncidents is itself an iterator method,
            // so nothing after a `yield return` runs until the caller asks for the next item.
            // Arc bookkeeping (RecordArcBeat) must not be deferred behind a `yield return` the
            // caller might never pull, so every ready event is converted and its bookkeeping
            // recorded BEFORE any incident from this pass is yielded.
            var readyEvents = EventQueue.PopReady(currentTick,
                () => map != null && GenHostility.AnyHostileActiveThreatToPlayer(map));
            var toFire = new List<FiringIncident>();
            foreach (var queued in readyEvents)
            {
                // Disease safety net — block even if it somehow got queued
                if (IsDisease(ResolveEventName(queued.EventType)) && !ColonyStateCollector.CanFireDisease())
                {
                    ClaudeLogger.LogEventSkipped($"Disease blocked by cooldown: {queued.EventType}");
                    if (queued.SourceCycle == "narrative")
                        StorytellerGameComponent.Get()?.RecordArcBeat(queued, null, "blocked_disease_cooldown", map);
                    continue;
                }

                // on_bad branch: classify the PREVIOUS fired beat's window before this beat's
                // own RecordArcBeat call appends a new record, then substitute if bad.
                if (queued.SourceCycle == "narrative" && !string.IsNullOrEmpty(queued.OnBadJson))
                {
                    var arcComp2 = StorytellerGameComponent.Get();
                    string classification = arcComp2?.ClassifyPreviousBeatWindow() ?? "ok";
                    if (classification == "bad")
                    {
                        string badType = ClaudeApiClient.ExtractOnBadString(queued.OnBadJson, "type");
                        if (!string.IsNullOrEmpty(badType))
                        {
                            ClaudeLogger.LogEntry("ARC_BRANCH_TAKEN",
                                $"Substituting on_bad branch for {queued.EventType} -> {badType} (previous beat window was bad).");
                            queued.EventType = badType;
                            queued.Subtype = ClaudeApiClient.ExtractOnBadString(queued.OnBadJson, "subtype") ?? queued.Subtype;
                            queued.Faction = ClaudeApiClient.ExtractOnBadString(queued.OnBadJson, "faction") ?? queued.Faction;
                            queued.Intensity = ClaudeApiClient.ExtractOnBadFloat(queued.OnBadJson, "intensity", queued.Intensity);
                            // on_bad has no pawn fields; the originals were chosen for the
                            // PLANNED type and would silently misdirect the substitute.
                            queued.PawnKindName = null;
                            queued.PawnCount = 0;
                            string badFlavor = ClaudeApiClient.ExtractOnBadString(queued.OnBadJson, "flavor");
                            if (!string.IsNullOrEmpty(badFlavor)) queued.Flavor = badFlavor;
                        }
                    }
                }

                // Slot tokens: fill {dead}/{downed}/{faction}/{days} before this flavor is ever
                // staged or sent; drop any sentence still containing an unfillable brace.
                if (!string.IsNullOrEmpty(queued.Flavor))
                    queued.Flavor = ApplySlotTokens(queued.Flavor, StorytellerGameComponent.Get());

                // type "none": a letter-only beat. Fixed in Phase 3 to actually honor
                // delay_hours — it used to fire the instant it was authored, ignoring the
                // queue entirely. Now it rides EventQueue like any other beat and is handled
                // here rather than converted to an incident.
                if (string.Equals(queued.EventType, "none", StringComparison.OrdinalIgnoreCase))
                {
                    var noneComp = StorytellerGameComponent.Get();
                    // Scattered vignettes share this path but are texture, not beats —
                    // they must never write into the arc's transcript or beat record.
                    bool isArcBeat = !string.Equals(queued.SourceCycle, "scattered", StringComparison.OrdinalIgnoreCase);
                    if (isArcBeat && !string.IsNullOrEmpty(queued.Flavor)) noneComp?.RecordArcLetter(queued.Flavor);
                    // Queued, not sent — delivered at the top of the NEXT pass, so it cannot
                    // clobber flavor staged for another beat firing later in THIS pass.
                    noneComp?.QueuePendingLetter(
                        isArcBeat ? (queued.ArcName ?? "The story continues") : "Around the colony",
                        queued.Flavor, false, false);
                    if (isArcBeat)
                        noneComp?.RecordLetterOnlyBeat(queued.CircleStep, queued.Link, queued.LinkReason, queued.Expect, queued.Faction, map);
                    continue;
                }

                var incident = ConvertQueuedToIncidentWithFallback(queued, target, out string resolvedType);
                if (incident != null)
                {
                    if (queued.SourceCycle == "narrative")
                        StorytellerGameComponent.Get()?.RecordArcBeat(queued, resolvedType, "fired", map);
                    toFire.Add(incident);
                }
                else if (queued.SourceCycle == "narrative")
                {
                    StorytellerGameComponent.Get()?.RecordArcBeat(queued, resolvedType, "failed", map);
                }
            }

            foreach (var incident in toFire) yield return incident;

            // ========== Check unified timer ==========
            // Source of truth lives on the GameComponent so it survives StorytellerComp being
            // recreated on load; the local field just mirrors it for same-session reads.
            var timingComp = StorytellerGameComponent.Get();
            int lastCallTick = timingComp?.LastUnifiedCallTick ?? lastUnifiedCallTick;
            if (lastCallTick < 0) lastCallTick = lastUnifiedCallTick;
            int requestedTick = timingComp?.RequestedCallTick ?? -1;

            int minGapTicks = (int)(UnifiedCallMinDays * GenDate.TicksPerDay);
            int floorTick = lastCallTick + minGapTicks;
            int scheduledTick = lastCallTick + nextUnifiedIntervalTicks;
            int targetTick = requestedTick >= 0 ? Math.Min(scheduledTick, requestedTick) : scheduledTick;
            int fireAtTick = Math.Max(floorTick, targetTick);

            if (currentTick >= fireAtTick && !unifiedCallInProgress)
            {
                lastUnifiedCallTick = currentTick;
                timingComp?.SetLastUnifiedCallTick(currentTick);
                timingComp?.ClearRequestedCallTick();
                StartUnifiedCall(map);
            }
        }

        // ========== Unified API Call ==========

        private async void StartUnifiedCall(Map map)
        {
            lock (lockObj) { if (unifiedCallInProgress) return; unifiedCallInProgress = true; }

            try
            {
                if (!ClaudeApiClient.CanMakeCall())
                {
                    ClaudeLogger.LogEventSkipped("Unified: Rate limited");
                    return;
                }

                var state = ColonyStateCollector.CollectState(map, "unified");
                if (state == null) { ClaudeLogger.LogApiError("Unified: CollectState returned null"); return; }

                var client = new ClaudeApiClient(ClaudeStorytellerMod.settings.ApiKey);
                var response = await client.GetUnifiedDecision(state);

                lock (lockObj)
                {
                    if (response != null)
                        pendingUnifiedResponse = response;
                }
            }
            catch (Exception ex) { ClaudeLogger.LogApiError("Unified call failed", ex.Message); }
            finally { lock (lockObj) { unifiedCallInProgress = false; } }
        }

        // ========== Process Unified Response ==========

        private IEnumerable<FiringIncident> ProcessUnifiedResponse(UnifiedResponse response, IIncidentTarget target)
        {
            Map map = target as Map;

            // Apply posture
            if (response.Posture != null && !string.IsNullOrEmpty(response.Posture.CurrentBlend))
            {
                var comp = StorytellerGameComponent.Get();
                if (comp != null)
                {
                    comp.LastPosture = response.Posture.CurrentBlend;
                    ClaudeLogger.LogEntry("POSTURE",
                        $"Blend: {response.Posture.CurrentBlend}. " +
                        $"Reason: {response.Posture.Reasoning}. " +
                        $"Next hint: {response.Posture.NextPostureHint}"
                    );
                }
            }

            // Set next unified call interval from Claude's response
            float nextCallDays = ClaudeStorytellerMod.settings.ClampCallDays(
                response.NextCallDays > 0 ? response.NextCallDays : 3f);

            nextUnifiedIntervalTicks = (int)(nextCallDays * GenDate.TicksPerDay);
            StorytellerGameComponent.Get()?.SetNextUnifiedIntervalTicks(nextUnifiedIntervalTicks);
            ClaudeLogger.LogEntry("SCHEDULER", $"Next unified call in {nextCallDays:F1} game days");

            // Process narrative arc decision
            if (response.Arc != null)
            {
                var comp = StorytellerGameComponent.Get();
                string decision = response.Arc.Decision ?? "skip";
                bool hasEvents = response.Arc.Events != null && response.Arc.Events.Count > 0;

                switch (decision)
                {
                    case "start_arc":
                        StartArcFromResponse(response.Arc, map);
                        break;

                    case "continue":
                        if (comp != null && string.IsNullOrEmpty(comp.ActiveArcName) && hasEvents)
                        {
                            ClaudeLogger.LogEntry("ARC_CONTINUE_WITHOUT_ACTIVE",
                                $"'continue' received with no active arc; treating as start_arc: {response.Arc.ArcName}");
                            StartArcFromResponse(response.Arc, map);
                        }
                        else if (hasEvents)
                        {
                            if (string.Equals(response.Arc.QueuedBeatsAction, "replace", StringComparison.OrdinalIgnoreCase))
                            {
                                EventQueue.ClearNarrativeFor(comp?.ActiveArcName);
                                ClaudeLogger.LogEntry("ARC_QUEUE_REPLACED",
                                    $"Cleared queued beats for arc '{comp?.ActiveArcName}' before enqueuing replacements " +
                                    "(queued_beats_action: replace).");
                            }
                            AppendArcBeats(response.Arc, map);
                        }
                        else
                        {
                            comp?.NoteEmptyContinue();
                        }
                        break;

                    case "end_arc":
                        comp?.SetUnresolvedThreads(response.Arc.UnresolvedThreads);
                        comp?.MarkArcClosing(response.Arc.ClosingFlavor);
                        if (hasEvents)
                        {
                            // At most one final beat on end_arc; the closing letter is
                            // delivered after it fires, not alongside the opening.
                            var finalArc = new NarrativeArcDecision
                            {
                                Decision = "end_arc",
                                ArcName = comp?.ActiveArcName ?? response.Arc.ArcName,
                                Events = new List<ArcEvent> { response.Arc.Events[0] },
                                ArcFlavor = null
                            };
                            AppendArcBeats(finalArc, map);
                        }
                        break;

                    case "skip":
                    default:
                        break;
                }

                // Applied AFTER the switch: start_arc resets summary/reserved-types/last-expectation
                // to null inside StartArc(), so setting these before the switch would be wiped out
                // immediately. "Store ArcSummarySoFar and the last beat's Expect every call."
                comp?.SetArcSummary(response.Arc.ArcSummarySoFar);
                if (response.Arc.ArcReservedTypes != null && response.Arc.ArcReservedTypes.Count > 0)
                    comp?.SetArcReservedTypes(response.Arc.ArcReservedTypes);
                if (hasEvents)
                {
                    string lastExpect = response.Arc.Events[response.Arc.Events.Count - 1].Expect;
                    comp?.SetArcLastExpectation(lastExpect);
                }
            }

            // Process scattered events — the world being alive around the arc
            if (response.ScatteredEvents != null && response.ScatteredEvents.Count > 0)
            {
                var scatteredList = TrimScatteredForActiveArc(response.ScatteredEvents, target);

                ClaudeLogger.LogEntry("SCATTERED",
                    $"Queueing {scatteredList.Count} scattered events across next {nextCallDays:F1} days"
                );

                foreach (var scattered in scatteredList)
                {
                    if (string.IsNullOrEmpty(scattered.Type)) continue;

                    // type "none": a letter-only vignette, no incident behind it. Always rides
                    // EventQueue — even at delay 0 — so slot tokens and the tell check run in
                    // the drain loop like every other letter; "next pass" is prompt enough for
                    // pure texture. One with no flavor is nothing at all: drop it.
                    if (string.Equals(scattered.Type, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(scattered.Flavor)) continue;
                        EventQueue.EnqueueDelayed(new QueuedEvent
                        {
                            EventType = "none",
                            SourceCycle = "scattered",
                            Note = scattered.Note,
                            Flavor = scattered.Flavor
                        }, Math.Max(scattered.DelayHours, 0f));
                        continue;
                    }

                    // Disease safety net
                    string resolvedType = ResolveEventName(scattered.Type);
                    if (IsDisease(resolvedType) && !ColonyStateCollector.CanFireDisease())
                    {
                        ClaudeLogger.LogEventSkipped($"Scattered disease blocked by cooldown: {scattered.Type}");
                        continue;
                    }

                    if (scattered.DelayHours > 0)
                    {
                        var queued = new QueuedEvent
                        {
                            EventType = scattered.Type,
                            Subtype = scattered.Subtype,
                            ArrivalMode = scattered.ArrivalMode,
                            PawnKindName = scattered.PawnKind,
                            PawnCount = scattered.PawnCount,
                            Faction = scattered.Faction,
                            Intensity = scattered.Intensity,
                            SourceCycle = "scattered",
                            Note = scattered.Note,
                            Flavor = scattered.Flavor
                        };
                        EventQueue.EnqueueDelayed(queued, scattered.DelayHours);
                    }
                    else
                    {
                        // Fire immediately — routed through the same QueuedEvent +
                        // ConvertQueuedToIncidentWithFallback path as the delayed branch above and
                        // every arc beat, so ArrivalMode threads through for free. This also fixes
                        // a pre-existing bug: only the queued path staged flavor, so an immediate
                        // (delay 0) scattered event's flavor was silently dropped before this.
                        var immediateQueued = new QueuedEvent
                        {
                            EventType = scattered.Type,
                            Subtype = scattered.Subtype,
                            ArrivalMode = scattered.ArrivalMode,
                            PawnKindName = scattered.PawnKind,
                            PawnCount = scattered.PawnCount,
                            Faction = scattered.Faction,
                            Intensity = scattered.Intensity,
                            SourceCycle = "scattered",
                            Note = scattered.Note,
                            Flavor = scattered.Flavor
                        };
                        var incident = ConvertQueuedToIncidentWithFallback(immediateQueued, target, out _);
                        if (incident != null) yield return incident;
                    }
                }
            }

            // Log overall reasoning
            if (!string.IsNullOrEmpty(response.OverallReasoning))
            {
                ClaudeLogger.LogEntry("UNIFIED_REASONING", response.OverallReasoning);
            }
        }

        /// <summary>
        /// While an arc is active, scattered events are "meanwhile": trims to at most
        /// settings.maxScatteredDuringArc, drops reserved types / the pinned arc faction
        /// (RESERVED_TYPE_DROPPED), and postpones (never drops) scattered threats that would
        /// fire within 24h of a queued arc beat (SCATTERED_TRIMMED). Positive events are never
        /// dropped for the cap before threats are.
        /// </summary>
        private List<ScatteredEvent> TrimScatteredForActiveArc(List<ScatteredEvent> scattered, IIncidentTarget target)
        {
            var comp = StorytellerGameComponent.Get();
            if (comp == null || string.IsNullOrEmpty(comp.ActiveArcName))
                return scattered;

            var reserved = new HashSet<string>(comp.GetArcReservedTypes() ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            string pinnedFaction = comp.ActiveArcFaction;
            var queuedArcBeats = EventQueue.PeekNarrativeFor(comp.ActiveArcName);
            int currentTick = Find.TickManager.TicksGame;
            int postponeWindowTicks = (int)(24f * GenDate.TicksPerHour);

            var kept = new List<ScatteredEvent>();
            foreach (var s in scattered)
            {
                string resolvedType = ResolveEventName(s.Type);

                if ((!string.IsNullOrEmpty(s.Type) && reserved.Contains(s.Type)) ||
                    (!string.IsNullOrEmpty(resolvedType) && reserved.Contains(resolvedType)))
                {
                    ClaudeLogger.LogEntry("RESERVED_TYPE_DROPPED",
                        $"Dropped scattered {s.Type}: reserved for arc '{comp.ActiveArcName}'.");
                    continue;
                }
                if (!string.IsNullOrEmpty(pinnedFaction) && !string.IsNullOrEmpty(s.Faction) &&
                    string.Equals(s.Faction, pinnedFaction, StringComparison.OrdinalIgnoreCase))
                {
                    ClaudeLogger.LogEntry("RESERVED_TYPE_DROPPED",
                        $"Dropped scattered {s.Type}: faction '{s.Faction}' is pinned to the active arc.");
                    continue;
                }

                bool isThreat = IsThreat(resolvedType);
                if (isThreat && queuedArcBeats.Count > 0)
                {
                    int fireTick = currentTick + (int)(s.DelayHours * GenDate.TicksPerHour);
                    bool tooClose = queuedArcBeats.Any(b => Math.Abs(b.FireAtTick - fireTick) < postponeWindowTicks);
                    if (tooClose)
                    {
                        s.DelayHours += 24f;
                        ClaudeLogger.LogEntry("SCATTERED_TRIMMED",
                            $"Postponed scattered threat {s.Type} by 24h (within a day of a queued arc beat).");
                    }
                }

                kept.Add(s);
            }

            int cap = (int)ClaudeStorytellerMod.settings.maxScatteredDuringArc;
            if (kept.Count <= cap) return kept;

            // Never drop a positive event for the cap before a threat is dropped.
            var positives = kept.Where(s => !IsThreat(ResolveEventName(s.Type))).ToList();
            var threats = kept.Where(s => IsThreat(ResolveEventName(s.Type))).ToList();

            var result = new List<ScatteredEvent>();
            foreach (var p in positives) { if (result.Count < cap) result.Add(p); }
            foreach (var t in threats) { if (result.Count < cap) result.Add(t); }

            int droppedCount = kept.Count - result.Count;
            if (droppedCount > 0)
                ClaudeLogger.LogEntry("SCATTERED_TRIMMED",
                    $"Trimmed {droppedCount} scattered event(s) beyond the cap of {cap} (threats dropped before positives).");

            return result;
        }

        private void StartArcFromResponse(NarrativeArcDecision arc, Map map)
        {
            var comp = StorytellerGameComponent.Get();
            EventQueue.ClearBySource("narrative");
            comp?.StartArc(arc.ArcName, arc.StoryQuestion, map);

            var resolvedFac = ResolveFaction(arc.ArcFaction);
            if (resolvedFac != null) comp?.SetArcFaction(resolvedFac.Name);

            ClaudeLogger.LogEntry("NARRATIVE_ARC",
                $"Started arc: {arc.ArcName}. Question: {arc.StoryQuestion}. {arc.Events?.Count ?? 0} beat(s).\n{arc.Reasoning}");

            string openingFlavor = ApplySlotTokens(arc.ArcFlavor, comp);
            SendNarrativeLetter(
                string.IsNullOrEmpty(arc.ArcName) ? "A story begins" : arc.ArcName,
                openingFlavor,
                LetterDefOf.NeutralEvent);
            comp?.RecordArcLetter(openingFlavor);

            EnqueueArcBeats(arc, comp, map);
        }

        private void AppendArcBeats(NarrativeArcDecision arc, Map map)
        {
            var comp = StorytellerGameComponent.Get();
            ClaudeLogger.LogEntry("NARRATIVE_ARC",
                $"Continuing arc: {comp?.ActiveArcName}. +{arc.Events?.Count ?? 0} beat(s).\n{arc.Reasoning}");
            // "continue" APPENDS — never clear the queue here, that would drop any still-pending
            // beat from a previous call (the exact "continue discards events" bug this fixes).
            EnqueueArcBeats(arc, comp, map);
        }

        private void EnqueueArcBeats(NarrativeArcDecision arc, StorytellerGameComponent comp, Map map)
        {
            if (arc.Events == null || arc.Events.Count == 0) return;

            var events = new List<ArcEvent>(arc.Events);

            // Hard cap: at most 2 arc beats authored per call.
            if (events.Count > 2)
            {
                int overCap = events.Count - 2;
                events = events.Take(2).ToList();
                ClaudeLogger.LogEntry("ARC_BEATS_TRIMMED",
                    $"Trimmed {overCap} beat(s) beyond the 2-per-call cap.");
            }
            // A second beat in the same call is only ever a "the first beat is OVER" dependency,
            // never a "how it went" dependency — that would be pre-scheduled causality in
            // disguise. If it isn't after_calm, drop it rather than queue an asserted link.
            if (events.Count == 2 && events[1].FireWhen != "after_calm")
            {
                events.RemoveAt(1);
                ClaudeLogger.LogEntry("ARC_BEATS_TRIMMED",
                    "Second beat trimmed: not fire_when \"after_calm\".");
            }

            // Steps already on record for this arc — fired beats plus whatever is still queued —
            // used for the link-violation and shape-warning checks below.
            var priorSteps = comp?.GetArcBeatSteps() ?? new List<string>();
            var alreadyQueued = EventQueue.PeekNarrativeFor(comp?.ActiveArcName);
            foreach (var q in alreadyQueued)
                if (!string.IsNullOrEmpty(q.CircleStep)) priorSteps.Add(q.CircleStep);

            bool isVeryFirstBeatOfArc = priorSteps.Count == 0 && alreadyQueued.Count == 0;
            bool hasLetterOnlyAlready = (comp?.HasLetterOnlyBeat() ?? false) ||
                alreadyQueued.Any(q => string.Equals(q.EventType, "none", StringComparison.OrdinalIgnoreCase));

            // Enforce minimum spacing between arc events
            float lastDelayHours = 0;
            bool hasDiseaseInArc = false;
            bool firstInThisBatch = true;

            foreach (var arcEvent in events)
            {
                float delayHours = arcEvent.DelayHours;

                // Enforce minimum spacing from previous event
                if (delayHours < lastDelayHours + ARC_EVENT_MIN_SPACING_HOURS && lastDelayHours > 0)
                {
                    delayHours = lastDelayHours + ARC_EVENT_MIN_SPACING_HOURS;
                    ClaudeLogger.LogEntry("ARC_SPACING",
                        $"Bumped {arcEvent.Type} from {arcEvent.DelayHours}h to {delayHours}h (minimum {ARC_EVENT_MIN_SPACING_HOURS}h gap)"
                    );
                }

                bool isArcFirstBeat = isVeryFirstBeatOfArc && firstInThisBatch;
                if (!isArcFirstBeat && (arcEvent.Link == "and" || string.IsNullOrEmpty(arcEvent.Link)))
                {
                    ClaudeLogger.LogEntry("ARC_LINK_VIOLATION",
                        $"Beat {arcEvent.Type} has link '{arcEvent.Link ?? "null"}' but is not the arc's first beat. Queuing anyway.");
                }

                if (arcEvent.CircleStep == "need" && priorSteps.Contains("need"))
                {
                    ClaudeLogger.LogEntry("ARC_SHAPE_WARNING",
                        $"'need' repeats in arc '{comp?.ActiveArcName}'.");
                }
                if (arcEvent.CircleStep == "take" && !priorSteps.Contains("search") && !priorSteps.Contains("find"))
                {
                    ClaudeLogger.LogEntry("ARC_SHAPE_WARNING",
                        $"'take' precedes any 'search'/'find' in arc '{comp?.ActiveArcName}'.");
                }
                if (!string.IsNullOrEmpty(arcEvent.CircleStep)) priorSteps.Add(arcEvent.CircleStep);
                firstInThisBatch = false;

                // type "none": a letter-only beat, at most one per arc. Queued like any other
                // beat so delay_hours is honored (Phase 2 fired it immediately on authoring,
                // which meant Claude's chosen timing was ignored); the letter is actually sent
                // and RecordLetterOnlyBeat called when it pops from EventQueue.
                if (string.Equals(arcEvent.Type, "none", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasLetterOnlyAlready)
                    {
                        ClaudeLogger.LogEntry("ARC_LETTER_ONLY_SKIPPED",
                            "A second letter-only beat was requested for this arc; skipped (max one).");
                        continue;
                    }
                    hasLetterOnlyAlready = true;
                    var queuedNone = new QueuedEvent
                    {
                        EventType = "none",
                        Faction = arcEvent.Faction,
                        SourceCycle = "narrative",
                        ArcName = comp?.ActiveArcName,
                        Note = arcEvent.Note,
                        Flavor = arcEvent.Flavor,
                        Link = arcEvent.Link,
                        LinkReason = arcEvent.LinkReason,
                        CircleStep = arcEvent.CircleStep,
                        Expect = arcEvent.Expect,
                        FireWhen = arcEvent.FireWhen
                    };
                    EventQueue.EnqueueDelayed(queuedNone, delayHours);
                    lastDelayHours = delayHours;
                    comp?.ResetCallsSinceBeatAuthored();
                    continue;
                }

                // Block multiple diseases in same arc
                string resolvedType = ResolveEventName(arcEvent.Type);
                if (IsDisease(resolvedType))
                {
                    if (hasDiseaseInArc)
                    {
                        ClaudeLogger.LogEntry("ARC_DISEASE_BLOCKED",
                            $"Blocked second disease in arc: {arcEvent.Type}. Skipping."
                        );
                        continue;
                    }

                    // Also check global disease cooldown
                    if (!ColonyStateCollector.CanFireDisease())
                    {
                        ClaudeLogger.LogEntry("ARC_DISEASE_BLOCKED",
                            $"Disease on cooldown: {arcEvent.Type}. Skipping."
                        );
                        continue;
                    }

                    hasDiseaseInArc = true;
                }

                var queued = new QueuedEvent
                {
                    EventType = arcEvent.Type,
                    Subtype = arcEvent.Subtype,
                    ArrivalMode = arcEvent.ArrivalMode,
                    PawnKindName = arcEvent.PawnKind,
                    PawnCount = arcEvent.PawnCount,
                    Faction = arcEvent.Faction,
                    Intensity = arcEvent.Intensity,
                    SourceCycle = "narrative",
                    ArcName = comp?.ActiveArcName,
                    Note = arcEvent.Note,
                    Flavor = arcEvent.Flavor,
                    Link = arcEvent.Link,
                    LinkReason = arcEvent.LinkReason,
                    CircleStep = arcEvent.CircleStep,
                    Expect = arcEvent.Expect,
                    FireWhen = arcEvent.FireWhen,
                    OnBadJson = arcEvent.OnBadJson
                };

                EventQueue.EnqueueDelayed(queued, delayHours);
                lastDelayHours = delayHours;
                comp?.ResetCallsSinceBeatAuthored();
            }
        }

        // ========== Player-Facing Narration ==========

        // Words that should never appear in a letter shown to the player — always suppress.
        // Deliberately does NOT include "arc", "beat", or "plot": those are substrings of
        // ordinary words ("architecture", "heartbeat", "plots of land") and would suppress
        // perfectly good in-world prose.
        private static readonly string[] HardMetaTells =
        {
            "cassandra", "phoebe", "randy", "storyteller", "narrative arc", "this arc",
            "pacing", "the player", "colonist count",
            "defname", "raid points", "wealth check", "sets tone", "structure is"
        };

        // Stems that are never a complete word on their own — "calibrat" is a prefix of
        // calibrate/calibration/calibrated but never appears bare, so wrapping it in \b...\b (a
        // TRAILING boundary too) can never match anything. These use a LEFT boundary only.
        private static readonly string[] HardMetaTellStems =
        {
            "calibrat"
        };

        // Words that sometimes leak reasoning but sometimes are just prose ("as a result of the
        // storm..."). Logged every time; only suppressed when two DISTINCT soft tells co-occur
        // in the same body, which is a much stronger signal of leaked reasoning than any one.
        // "difficulty"/"intensity" live here, not in HardMetaTells: both are ordinary English
        // words a colony letter can use honestly ("the difficulty of the crossing", "the
        // intensity of the storm"); only two co-occurring soft tells are a strong enough signal
        // to suppress on.
        private static readonly string[] SoftMetaTells =
        {
            "because of", "as a result", "therefore", "consequence", "lesson", "storyline", "status quo",
            "difficulty", "intensity"
        };

        private static readonly Dictionary<string, Regex> HardTellRegexes = BuildTellRegexes(HardMetaTells);
        private static readonly Dictionary<string, Regex> HardTellStemRegexes = BuildStemRegexes(HardMetaTellStems);
        private static readonly Dictionary<string, Regex> SoftTellRegexes = BuildTellRegexes(SoftMetaTells);

        private static Dictionary<string, Regex> BuildTellRegexes(string[] terms)
        {
            var map = new Dictionary<string, Regex>();
            foreach (var term in terms)
            {
                // Multi-word phrases use plain literal matching (word boundaries around a
                // phrase with internal spaces behave the same as a substring match here);
                // single words get \b...\b so "arch" style false positives are structurally
                // impossible even if a term is ever added carelessly later.
                string pattern = term.Contains(" ")
                    ? Regex.Escape(term)
                    : $@"\b{Regex.Escape(term)}\b";
                map[term] = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            return map;
        }

        // Left-boundary-only: for stems that are prefixes of real words and can never satisfy a
        // trailing \b. Still anchored on the left so "recalibrat-" mid-word matches are the only
        // thing intended to match (a plain substring search with no boundary at all would also
        // hit unrelated words that merely contain the letters).
        private static Dictionary<string, Regex> BuildStemRegexes(string[] terms)
        {
            var map = new Dictionary<string, Regex>();
            foreach (var term in terms)
                map[term] = new Regex($@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return map;
        }

        // Phase 3: names.  A capitalized word 3+ letters long that is not a known colonist,
        // faction, the arc name, or an ordinary sentence-starter is very likely a hallucinated
        // name slipping past "use colonist_names or {dead}/{downed} only". Logged, NEVER
        // suppressed — this is an audit trail, not a filter.
        private static readonly Regex CapitalizedWordRegex = new Regex(@"\b[A-Z][A-Za-z]{2,}\b", RegexOptions.Compiled);

        private static readonly HashSet<string> SentenceStartAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "this", "that", "these", "those", "it", "its", "they", "their",
            "he", "his", "she", "her", "you", "your", "we", "our", "i",
            "but", "and", "or", "so", "if", "when", "while", "after", "before", "then",
            "there", "here", "now", "today", "tomorrow", "yesterday", "still", "never", "always",
            "no", "yes",
            "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"
        };

        public static void CheckNameUnverified(string body)
        {
            if (string.IsNullOrEmpty(body)) return;
            try
            {
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var map = Find.CurrentMap;
                if (map?.mapPawns?.FreeColonists != null)
                    foreach (var p in map.mapPawns.FreeColonists)
                        if (p.Name != null) known.Add(p.Name.ToStringShort);

                if (Find.FactionManager != null)
                    foreach (var fac in Find.FactionManager.AllFactions)
                        if (!string.IsNullOrEmpty(fac.Name)) known.Add(fac.Name);

                string arcName = StorytellerGameComponent.Get()?.ActiveArcName;
                if (!string.IsNullOrEmpty(arcName)) known.Add(arcName);

                foreach (Match m in CapitalizedWordRegex.Matches(body))
                {
                    string word = m.Value;
                    if (SentenceStartAllowlist.Contains(word)) continue;
                    if (known.Any(k => k.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                    ClaudeLogger.LogEntry("NAME_UNVERIFIED",
                        $"Unverified capitalized token '{word}' in player-facing text: {body}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] CheckNameUnverified failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shared MetaTells gate: true if body is clean of leaked storyteller reasoning, false
        /// (with a reason) otherwise. Shared so both delivery paths — SendNarrativeLetter's own
        /// letters AND the Harmony merge path in LetterFlavorPatch.Prefix, which used to deliver
        /// flavor straight to the player with no check at all — apply the exact same rule.
        /// </summary>
        public static bool PassesTells(string body, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(body)) return true;

            // Hard tells: always suppress. Safety net for leaked reasoning that would break
            // immersion outright ("Structure is Cassandra-clean...").
            foreach (var kvp in HardTellRegexes)
            {
                if (kvp.Value.IsMatch(body))
                {
                    reason = $"hard tell '{kvp.Key}'";
                    return false;
                }
            }
            foreach (var kvp in HardTellStemRegexes)
            {
                if (kvp.Value.IsMatch(body))
                {
                    reason = $"hard tell stem '{kvp.Key}'";
                    return false;
                }
            }

            // Soft tells: log every hit, but only suppress on two DISTINCT co-occurring terms —
            // a single "therefore" is often just prose, two together is a strong leak signal.
            var softHits = new List<string>();
            foreach (var kvp in SoftTellRegexes)
            {
                if (kvp.Value.IsMatch(body))
                    softHits.Add(kvp.Key);
            }
            if (softHits.Count > 0)
            {
                ClaudeLogger.LogEntry("SOFT_TELL",
                    $"Player-facing text contained soft tell(s) [{string.Join(", ", softHits)}]:" + Environment.NewLine + body);
            }
            if (softHits.Count >= 2)
            {
                reason = $"{softHits.Count} co-occurring soft tells [{string.Join(", ", softHits)}]";
                return false;
            }

            return true;
        }

        private static void SendNarrativeLetter(string label, string body, LetterDef def)
        {
            if (!ClaudeStorytellerMod.settings.showNarrativeLetters) return;
            if (string.IsNullOrEmpty(body)) return;

            if (!PassesTells(body, out string reason))
            {
                ClaudeLogger.LogEntry("LETTER_SUPPRESSED",
                    $"Player-facing text failed tell check ({reason}), not shown:" + Environment.NewLine + body);
                return;
            }

            CheckNameUnverified(body);

            try
            {
                if (Current.Game == null || Find.LetterStack == null) return;
                // Never let this letter absorb a DIFFERENT flavor entry staged for some other
                // incident — see LetterFlavorPatch.Prefix's SelfSendActive guard.
                LetterFlavorPatch.SelfSendActive = true;
                try
                {
                    LetterDef resolved = def ?? LetterDefOf.NeutralEvent;
                    // Mod-sent narration (arc openers, letter-only beats, closing letters)
                    // gets the same violet as merged letters; threat defs pass through.
                    if (resolved == LetterDefOf.NeutralEvent)
                        resolved = LetterFlavorPatch.NarrativeLetter ?? resolved;
                    Find.LetterStack.ReceiveLetter(label, body, resolved);
                }
                finally
                {
                    LetterFlavorPatch.SelfSendActive = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] SendNarrativeLetter failed: {ex.Message}");
            }
        }

        // ========== Slot tokens ==========

        // Matches any surviving {word} token so a sentence with an unfillable slot can be
        // dropped rather than shown to the player with a literal "{dead}" in it.
        private static readonly Regex UnfilledSlotRegex = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);

        /// <summary>
        /// Replaces {dead}, {downed}, {faction}, {days} with real values, then drops any
        /// sentence that still contains an unfilled "{...}" token (logs SLOT_DROPPED). Sentences
        /// are split on ". " so one bad slot does not lose the whole letter.
        /// </summary>
        private static string ApplySlotTokens(string text, StorytellerGameComponent comp)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string filled = text
                .Replace("{dead}", comp?.LastDeathName ?? "{dead}")
                .Replace("{downed}", comp?.LastDownedName ?? "{downed}")
                .Replace("{faction}", comp?.ActiveArcFaction ?? "{faction}")
                .Replace("{days}", comp != null ? (GenDate.DaysPassed - comp.GetActiveArcStartDay()).ToString() : "{days}");

            var sentences = filled.Split(new[] { ". " }, StringSplitOptions.None);
            var kept = new List<string>();
            for (int i = 0; i < sentences.Length; i++)
            {
                string sentence = sentences[i];
                if (UnfilledSlotRegex.IsMatch(sentence))
                {
                    ClaudeLogger.LogEntry("SLOT_DROPPED", $"Dropped sentence with unfilled slot: {sentence}");
                    continue;
                }
                kept.Add(sentence);
            }

            string result = string.Join(". ", kept);
            // Restore the trailing period the split above ate, if the original text had one and
            // the last kept sentence does not already end with terminal punctuation.
            if (result.Length > 0 && !result.EndsWith(".") && !result.EndsWith("!") && !result.EndsWith("?")
                && (text.EndsWith(".") || text.EndsWith("!") || text.EndsWith("?")))
            {
                result += ".";
            }
            return result;
        }

        // ========== Event Conversion ==========

        private FiringIncident ConvertQueuedToIncidentWithFallback(QueuedEvent queued, IIncidentTarget target, out string resolvedType)
        {
            var incident = ConvertToIncidentWithFallback(
                queued.EventType,
                queued.Intensity,
                queued.Faction,
                queued.Subtype,
                queued.ArrivalMode,
                queued.PawnKindName,
                queued.PawnCount,
                target,
                queued.SourceCycle,
                out resolvedType
            );

            // Only narrate events that actually fired and carry a note from Claude.
            if (incident != null && !string.IsNullOrEmpty(queued.Flavor)
                && ClaudeStorytellerMod.settings.showNarrativeLetters)
            {
                // The Harmony merge path (LetterFlavorPatch.Prefix) delivers this flavor straight
                // into whatever letter the game raises next, WITHOUT going through
                // SendNarrativeLetter — so the MetaTells check has to happen here, before
                // staging, or a leaked-reasoning body reaches the player unfiltered.
                if (!PassesTells(queued.Flavor, out string tellReason))
                {
                    ClaudeLogger.LogEntry("LETTER_SUPPRESSED",
                        $"Flavor for {queued.EventType} failed tell check ({tellReason}), not staged:" +
                        Environment.NewLine + queued.Flavor);
                    return incident;
                }

                bool isArc = !string.IsNullOrEmpty(queued.ArcName);
                string label = string.IsNullOrEmpty(queued.ArcName) ? "The world turns" : queued.ArcName;
                bool isThreat = IsThreat(ResolveEventName(queued.EventType));

                if (LetterFlavorPatch.PatchActive)
                {
                    // Merge into the letter this incident is about to send.
                    LetterFlavorPatch.StageFlavor(queued.Flavor, isArc, label, isThreat);
                }
                else
                {
                    // Patch unavailable - fall back to a standalone letter.
                    SendNarrativeLetter(label, queued.Flavor,
                        isThreat ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent);
                    if (isArc) StorytellerGameComponent.Get()?.RecordArcLetter(queued.Flavor);
                }
            }

            return incident;
        }

        private FiringIncident ConvertToIncidentWithFallback(string type, float intensity, string faction, string subtype, string arrivalMode, string pawnKind, int pawnCount, IIncidentTarget target, string source, out string resolvedType)
        {
            // Try the requested event first
            var incident = TryConvertToIncident(type, intensity, faction, subtype, arrivalMode, pawnKind, pawnCount, target);
            if (incident != null)
            {
                resolvedType = ResolveEventName(type);
                ClaudeLogger.LogEventFired(type, intensity, faction, subtype, incident.parms?.points ?? 0);
                ColonyStateCollector.RecordEvent(resolvedType, "fired", type, source);
                return incident;
            }

            // Narrative beats never take a random MinorFallback — record fizzled and let the
            // arc timing self-correct via an early call, rather than silently substituting an
            // unrelated incident into what is supposed to be an authored beat.
            if (source == "narrative")
            {
                resolvedType = null;
                ClaudeLogger.LogEntry("FALLBACK_SKIPPED_NARRATIVE",
                    $"Narrative beat {type} could not fire; not substituting a random fallback.");
                StorytellerGameComponent.Get()?.RequestEarlyCall($"narrative beat {type} fizzled");
                ColonyStateCollector.RecordEvent(type, "fizzled", type, source);
                return null;
            }

            // Pick fallback list based on source
            List<string> fallbacks = (source == "major") ? MajorFallbacks : MinorFallbacks;

            ClaudeLogger.LogEntry("FALLBACK", $"Primary event {type} failed, trying fallbacks...");

            // Shuffle fallbacks for variety
            var shuffled = new List<string>(fallbacks);
            var rand = new Random();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                var temp = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = temp;
            }

            foreach (var fallbackType in shuffled)
            {
                incident = TryConvertToIncident(fallbackType, 1.0f, null, null, null, null, 0, target);
                if (incident != null)
                {
                    resolvedType = fallbackType;
                    ClaudeLogger.LogEntry("FALLBACK_FIRED", $"Fallback event {fallbackType} fired instead of {type}");
                    ColonyStateCollector.RecordEvent(fallbackType, "fallback", type, source);
                    return incident;
                }
            }

            resolvedType = null;
            ClaudeLogger.LogEventSkipped($"All fallbacks failed for {type}");
            return null;
        }

        private FiringIncident TryConvertToIncident(string type, float intensity, string faction, string subtype, string arrivalMode, string pawnKind, int pawnCount, IIncidentTarget target)
        {
            var diff = GetDifficulty();
            string resolvedType = ResolveEventName(type);

            if (!diff.AllowThreats && IsThreat(resolvedType))
            {
                ClaudeLogger.LogEventSkipped($"Difficulty [{diff.Label}] blocks threat: {type}");
                return null;
            }

            if (!diff.AllowMajorThreats && IsMajorThreat(resolvedType))
            {
                ClaudeLogger.LogEventSkipped($"Difficulty [{diff.Label}] blocks major threat: {type}");
                return null;
            }

            // Disease safety net
            if (IsDisease(resolvedType) && !ColonyStateCollector.CanFireDisease())
            {
                ClaudeLogger.LogEventSkipped($"Disease cooldown active, blocking: {type}");
                return null;
            }

            float clampedIntensity = Math.Max(diff.MinIntensity, Math.Min(intensity, diff.MaxIntensity));

            IncidentDef incidentDef = DefDatabase<IncidentDef>.GetNamedSilentFail(resolvedType);

            // Not a known IncidentDef — try it as a named QuestScriptDef instead. Fires through
            // the ordinary GiveQuest_Random incident with a specific script pre-selected, so
            // everything downstream (target resolution, points, CanFireNow, letter merge, arc
            // records) works unchanged. Quest script names are naturally non-threat/non-disease
            // for every gate above, so nothing needs to special-case them earlier in this method.
            QuestScriptDef questScript = null;
            if (incidentDef == null)
            {
                questScript = DefDatabase<QuestScriptDef>.GetNamedSilentFail(resolvedType);
                if (questScript != null)
                {
                    incidentDef = DefDatabase<IncidentDef>.GetNamedSilentFail("GiveQuest_Random");
                    ClaudeLogger.LogEntry("QUEST_SCRIPT", $"Named quest script requested: {resolvedType}");
                }
            }
            if (incidentDef == null)
            {
                ClaudeLogger.LogEventSkipped($"Unknown incident def: {type} (resolved: {resolvedType})");
                return null;
            }

            // World-targeted incidents (Eclipse, GiveQuest_Random, mod world conditions) can only
            // fire against Find.World; vanilla's RandomQuest comp does the same during the World
            // pass. We only run in the Map pass, so pick the target per def here.
            IIncidentTarget fireTarget = ColonyStateCollector.ResolveTarget(incidentDef, target as Map);
            if (fireTarget == null)
            {
                ClaudeLogger.LogEventSkipped($"No allowed target for {type} (resolved: {resolvedType}; tags: " +
                    string.Join(",", incidentDef.targetTags?.Select(t => t.defName) ?? Enumerable.Empty<string>()) + ")");
                return null;
            }

            var parms = StorytellerUtility.DefaultParmsNow(incidentDef.category, fireTarget);
            parms.points *= clampedIntensity;

            if (questScript != null)
                parms.questScriptDef = questScript;

            if (!string.IsNullOrEmpty(faction))
            {
                Faction factionObj = ResolveFaction(faction);
                if (factionObj != null)
                {
                    // A threat can only be attributed to a faction actually hostile to the
                    // player — otherwise a friendly/neutral override would silently attach a
                    // raid to an ally. Non-threat overrides (quest asker, arc framing) are fine.
                    if (IsThreat(resolvedType) && !factionObj.HostileTo(Faction.OfPlayer))
                    {
                        ClaudeLogger.LogEntry("FACTION_NOT_HOSTILE",
                            $"Ignoring faction override '{faction}' -> {factionObj.Name} for threat {type}: not hostile to player.");
                    }
                    else
                    {
                        parms.faction = factionObj;
                    }
                }
            }

            if (!string.IsNullOrEmpty(subtype) && resolvedType.Contains("Raid"))
            {
                var strategy = GetRaidStrategy(subtype);
                if (strategy != null)
                    parms.raidStrategy = strategy;
            }

            if (resolvedType.Contains("Raid"))
            {
                PawnsArrivalModeDef mode = GetArrivalMode(arrivalMode);

                // Legacy nicety: "drop_pods" used to be (wrongly) mapped to a raid STRATEGY, so
                // it silently walked in. It is a real strategy now (see GetRaidStrategy) — give
                // it an actual drop when the beat did not also specify its own arrival_mode.
                if (mode == null && string.Equals(subtype, "drop_pods", StringComparison.OrdinalIgnoreCase))
                    mode = DefDatabase<PawnsArrivalModeDef>.GetNamedSilentFail("CenterDrop");

                if (mode != null)
                {
                    // Vanilla's ResolveRaidStrategy filters candidate strategies to ones
                    // compatible with a pre-set raidArrivalMode; going the other way, an
                    // incompatible pairing has to be caught here or the raid simply never
                    // constructs pawns for the mode it was told to use. Skip the override and
                    // let vanilla's own ResolveRaidArriveMode pick instead.
                    if (parms.raidStrategy != null && parms.raidStrategy.arriveModes != null
                        && !parms.raidStrategy.arriveModes.Contains(mode))
                    {
                        ClaudeLogger.LogEntry("ARRIVAL_INCOMPATIBLE",
                            $"arrival_mode '{arrivalMode ?? subtype}' -> {mode.defName} incompatible with raid strategy " +
                            $"{parms.raidStrategy.defName}; letting vanilla resolve arrival instead.");
                    }
                    else
                    {
                        parms.raidArrivalMode = mode;
                    }
                }
            }

            // pawn_kind / pawn_count: exactly two vanilla workers read these parms (verified
            // against this install's decompiled 1.6 assembly) — setting them anywhere else is a
            // silent no-op, so gate on the worker type, not the defName, which also covers mod
            // defs that reuse or subclass the vanilla workers.
            if (!string.IsNullOrEmpty(pawnKind) || pawnCount > 0)
            {
                bool isAnimalPack = incidentDef.workerClass != null
                    && typeof(IncidentWorker_AggressiveAnimals).IsAssignableFrom(incidentDef.workerClass);
                bool isRaid = incidentDef.workerClass != null
                    && typeof(IncidentWorker_RaidEnemy).IsAssignableFrom(incidentDef.workerClass);

                PawnKindDef kindDef = string.IsNullOrEmpty(pawnKind)
                    ? null
                    : DefDatabase<PawnKindDef>.GetNamedSilentFail(pawnKind);
                if (!string.IsNullOrEmpty(pawnKind) && kindDef == null)
                {
                    ClaudeLogger.LogEntry("PAWN_KIND_UNKNOWN",
                        $"pawn_kind '{pawnKind}' is not a PawnKindDef; ignoring for {type}.");
                }

                if (isAnimalPack)
                {
                    // Mirrors vanilla's private AggressiveAnimalIncidentUtility.CanArriveManhunter.
                    if (kindDef != null && !(kindDef.RaceProps.Animal && kindDef.canArriveManhunter
                        && kindDef.RaceProps.CanPassFences))
                    {
                        ClaudeLogger.LogEntry("PAWN_KIND_INCOMPATIBLE",
                            $"pawn_kind '{pawnKind}' cannot arrive as a manhunter; letting vanilla pick for {type}.");
                        kindDef = null;
                    }
                    if (kindDef != null) parms.pawnKind = kindDef;
                    if (pawnCount > 0)
                    {
                        // An explicit count bypasses the points formula entirely, so keep it
                        // sane: budget slack of 2.5x with a floor of 10 (swarms of small things
                        // are legitimate), under vanilla's own hard cap of 100.
                        float power = parms.pawnKind?.combatPower ?? 0f;
                        int maxByPoints = power > 0f ? Math.Max(10, (int)(parms.points * 2.5f / power)) : 100;
                        int clamped = Math.Min(Math.Min(pawnCount, maxByPoints), 100);
                        if (clamped != pawnCount)
                            ClaudeLogger.LogEntry("PAWN_COUNT_CLAMPED",
                                $"pawn_count {pawnCount} -> {clamped} for {type} ({parms.points:F0} points).");
                        parms.pawnCount = clamped;
                    }
                    if (parms.pawnKind != null || parms.pawnCount > 0)
                        ClaudeLogger.LogEntry("PAWN_KIND",
                            $"{type}: kind={parms.pawnKind?.defName ?? "(vanilla picks)"}, " +
                            $"count={(parms.pawnCount > 0 ? parms.pawnCount.ToString() : "(auto)")}");
                }
                else if (isRaid)
                {
                    // RaidStrategyWorker.SpawnThreats builds exactly pawnCount pawns of pawnKind
                    // when the kind is set — and an EMPTY (not null) list when the count is 0,
                    // which fires a raid with no pawns at all. Kind never goes on without a
                    // count >= 1. Mechs can only generate for the mechanoid faction.
                    if (kindDef != null && kindDef.RaceProps.IsMechanoid
                        && parms.faction != Faction.OfMechanoids)
                    {
                        ClaudeLogger.LogEntry("PAWN_KIND_INCOMPATIBLE",
                            $"pawn_kind '{pawnKind}' is mechanoid but the raid faction is " +
                            $"{parms.faction?.Name ?? "(unset)"}; ignoring for {type}.");
                        kindDef = null;
                    }
                    if (kindDef != null)
                    {
                        float power = Math.Max(1f, kindDef.combatPower);
                        int count = pawnCount > 0 ? pawnCount : Math.Max(1, (int)(parms.points / power));
                        // Tighter slack than animal packs — uniform raids kill colonies.
                        int maxByPoints = Math.Max(2, (int)(parms.points * 1.5f / power));
                        int clamped = Math.Max(1, Math.Min(Math.Min(count, maxByPoints), 50));
                        if (clamped != count)
                            ClaudeLogger.LogEntry("PAWN_COUNT_CLAMPED",
                                $"pawn_count {count} -> {clamped} for {type} ({parms.points:F0} points, " +
                                $"{kindDef.defName} power {kindDef.combatPower:F0}).");
                        parms.pawnKind = kindDef;
                        parms.pawnCount = clamped;
                        ClaudeLogger.LogEntry("PAWN_KIND",
                            $"{type}: raid of {parms.pawnCount} x {kindDef.defName}");
                    }
                    else if (pawnCount > 0 && string.IsNullOrEmpty(pawnKind))
                    {
                        ClaudeLogger.LogEntry("PAWN_KIND_INCOMPATIBLE",
                            $"pawn_count without pawn_kind does nothing on a raid; ignoring for {type}.");
                    }
                }
                else
                {
                    ClaudeLogger.LogEntry("PAWN_KIND_INCOMPATIBLE",
                        $"{type} does not honor pawn_kind/pawn_count (worker " +
                        $"{incidentDef.workerClass?.Name ?? "null"}); ignoring.");
                }
            }

            // Check if the event can actually fire
            if (!incidentDef.Worker.CanFireNow(parms))
            {
                ClaudeLogger.LogEventSkipped($"CanFireNow false: {type} (resolved: {resolvedType})");
                return null;
            }

            return new FiringIncident(incidentDef, this, parms);
        }

        /// <summary>
        /// Resolves a faction request: "same_as_opening" pins to the active arc's faction, an
        /// exact case-insensitive Faction.Name match is tried next, and only then does this
        /// fall back to the original Tribal/Pirate/Mechanoid kind match. Logs
        /// FACTION_UNRESOLVED when nothing matches, so "the same tribe comes back" never
        /// silently attaches to a different faction.
        /// </summary>
        private Faction ResolveFaction(string factionType)
        {
            if (string.IsNullOrEmpty(factionType)) return null;

            if (factionType == "same_as_opening")
            {
                string pinned = StorytellerGameComponent.Get()?.ActiveArcFaction;
                if (string.IsNullOrEmpty(pinned)) return null;
                factionType = pinned;
            }

            foreach (var fac in Find.FactionManager.AllFactions)
            {
                if (fac.Hidden || fac.defeated) continue;
                if (string.Equals(fac.Name, factionType, StringComparison.OrdinalIgnoreCase))
                    return fac;
            }

            foreach (var fac in Find.FactionManager.AllFactions)
            {
                if (!fac.HostileTo(Faction.OfPlayer)) continue;

                if (factionType == "Tribal" && fac.def.techLevel <= TechLevel.Neolithic)
                    return fac;
                if (factionType == "Pirate" && fac.def == FactionDefOf.Pirate)
                    return fac;
                if (factionType == "Mechanoid" && fac.def == FactionDefOf.Mechanoid)
                    return fac;
            }

            ClaudeLogger.LogEntry("FACTION_UNRESOLVED", $"No match for faction request '{factionType}'.");
            return null;
        }

        private RaidStrategyDef GetRaidStrategy(string subtype)
        {
            switch (subtype.ToLower())
            {
                case "sapper":
                    return DefDatabase<RaidStrategyDef>.GetNamedSilentFail("ImmediateAttackSappers");
                case "siege":
                    return DefDatabase<RaidStrategyDef>.GetNamedSilentFail("Siege");
                case "breach":
                    return DefDatabase<RaidStrategyDef>.GetNamedSilentFail("ImmediateAttackBreaching");
                case "drop_pods":
                    // Was "ImmediateAttackSmart" — a STRATEGY, not an arrival mode, so asking for
                    // drop pods actually got smart walk-ins. Plain ImmediateAttack now; the drop
                    // itself is applied as an arrival_mode override in TryConvertToIncident.
                    return DefDatabase<RaidStrategyDef>.GetNamedSilentFail("ImmediateAttack");
                case "assault":
                default:
                    return DefDatabase<RaidStrategyDef>.GetNamedSilentFail("ImmediateAttack");
            }
        }

        /// <summary>
        /// Maps the five friendly arrival_mode names to their PawnsArrivalModeDef, falling back
        /// to treating the input as an exact defName (mirrors GetRaidStrategy's shape). Returns
        /// null for an empty/unrecognized value rather than throwing — callers treat null as
        /// "no override, let vanilla decide".
        /// </summary>
        private static PawnsArrivalModeDef GetArrivalMode(string arrivalMode)
        {
            if (string.IsNullOrEmpty(arrivalMode)) return null;

            string defName;
            switch (arrivalMode.ToLower())
            {
                case "walk_in": defName = "EdgeWalkIn"; break;
                case "walk_in_groups": defName = "EdgeWalkInGroups"; break;
                case "drop_edge": defName = "EdgeDrop"; break;
                case "drop_center": defName = "CenterDrop"; break;
                case "drop_scatter": defName = "RandomDrop"; break;
                default: defName = arrivalMode; break; // accept an exact defName as a fallback
            }

            return DefDatabase<PawnsArrivalModeDef>.GetNamedSilentFail(defName);
        }
    }
}
