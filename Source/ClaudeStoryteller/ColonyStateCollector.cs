using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using ClaudeStoryteller.Models;

namespace ClaudeStoryteller
{
    [StaticConstructorOnStartup]
    public static class ColonyStateCollector
    {
        // Event categories for Claude to understand what's available
        private static readonly HashSet<string> WeatherEvents = new HashSet<string>
        {
            "ColdSnap", "HeatWave", "ToxicFallout", "VolcanicWinter", "Flashstorm", "Eclipse", "SolarFlare", "Aurora",
            // Biotech
            "NoxiousHaze",
            // Odyssey
            "VolcanicAsh", "Drought", "SeasonalFlooding", "BioluminescentSpores", "OrbitalDebris"
        };

        private static readonly HashSet<string> ThreatEventSet = new HashSet<string>
        {
            "RaidEnemy", "Infestation", "MechCluster", "ManhunterPack", "DefoliatorShipPartCrash",
            "PsychicEmanatorShipPartCrash", "PsychicDrone",
            // Core
            "Ambush", "ManhunterAmbush", "AnimalInsanityMass", "AnimalInsanitySingle",
            "DeepDrillInfestation", "CaravanDemand", "RansomDemand",
            // Royalty
            "ProblemCauser",
            // Anomaly
            "ChimeraAssault", "DevourerAssault", "GorehulkAssault", "FleshbeastAttack",
            "ShamblerSwarm", "SmallShamblerSwarm", "ShamblerAssault", "GhoulAttack",
            "SightstealerSwarm", "SightstealerArrival", "FrenziedAnimals",
            "BloodRain", "DeathPall", "UnnaturalDarkness"
        };

        private static readonly HashSet<string> PositiveEvents = new HashSet<string>
        {
            "TraderCaravanArrival", "OrbitalTraderArrival", "WandererJoin", "ResourcePodCrash",
            "RefugeePodCrash", "TravelerGroup", "VisitorGroup", "SelfTame", "FarmAnimalsWanderIn",
            "ThrumboPasses", "WildManWandersIn", "ShipChunkDrop",
            // Core
            "CaravanMeeting", "PsychicSoothe", "GiveQuest_Random",
            // Royalty
            "CaravanArrivalTributeCollector",
            // Ideology
            "GiveQuest_Beggars", "GiveQuest_ReliquaryPilgrims", "GiveQuest_WorkSite",
            // Anomaly
            "GoldenCubeArrival", "CreepJoinerJoin"
        };

        private static readonly HashSet<string> DiseaseEvents = new HashSet<string>
        {
            "Disease_Plague", "Disease_Flu", "Disease_Malaria", "Disease_GutWorms",
            "Disease_FibrousMechanites", "Disease_SensoryMechanites", "Disease_MuscleParasites",
            // Core
            "Disease_SleepingSickness", "Disease_OrganDecay", "Disease_AnimalFlu", "Disease_AnimalPlague",
            // Royalty
            "Disease_Abasia", "Disease_BloodRot",
            // Odyssey
            "GillRot"
        };

        // All known events for underused detection
        private static readonly List<string> AllKnownEvents = new List<string>();

        static ColonyStateCollector()
        {
            AllKnownEvents.AddRange(WeatherEvents);
            AllKnownEvents.AddRange(ThreatEventSet);
            AllKnownEvents.AddRange(PositiveEvents);
            AllKnownEvents.AddRange(DiseaseEvents);
            AllKnownEvents.Add("HerdMigration");
            AllKnownEvents.Add("AmbrosiaSprout");
            AllKnownEvents.Add("CropBlight");
            AllKnownEvents.Add("ShortCircuit");
            AllKnownEvents.Add("Alphabeavers");
            AllKnownEvents.Add("MeteoriteImpact");

            // [StaticConstructorOnStartup] means this runs after every def is loaded, so the
            // sweep happens once at startup rather than inside the first API call's latency.
            // It also makes EVENT_POOL_DISCOVERED a load-time health signal you can grep for
            // without waiting for a storyteller call. EnsureDiscovered stays idempotent and
            // is still called defensively from the accessors below.
            try { EnsureDiscovered(); }
            catch (Exception e)
            {
                // An exception escaping a static constructor makes every later access to
                // this type throw TypeInitializationException. Discovery is an enhancement;
                // it must never be able to take the collector down with it — and neither may
                // this handler, so the file logger is itself guarded and Verse.Log (safe before
                // a game exists) carries the message if the file logger cannot.
                try { ClaudeLogger.LogEntry("EVENT_POOL_DISCOVERY_FAILED", e.ToString()); }
                catch { }
                try { Log.Error("[ClaudeStoryteller] EVENT_POOL_DISCOVERY_FAILED: " + e); }
                catch { }
            }
        }

        // ========== Runtime discovery ==========
        // The sets above are the curated vanilla/DLC seed. Everything else the modlist
        // provides is discovered from the DefDatabase on first use, so a new event mod is
        // picked up without a code change. Seed membership always wins: a hand-classified
        // vanilla name keeps its category no matter what its def says.

        private static bool _discovered = false;

        // defName -> human label, for discovered defs only. Claude knows what
        // "ToxicFallout" means; it does not know what "LE_RiseTrees" means.
        private static readonly Dictionary<string, string> DiscoveredLabels =
            new Dictionary<string, string>();

        // Scripted/endgame incidents are storyteller-fireable in principle but are never
        // a sensible narrative beat. Everything else is offered and left to CanFireNow.
        private static readonly HashSet<string> ExcludedCategories = new HashSet<string>
        {
            "EndGame"
        };

        // Script-only incidents that became reachable once World targets were allowed. Each is
        // fired by a BaseStoryteller comp on its own schedule (ship escape day 20, Royal Ascent
        // every 22 days, intro quests day 8/26) or by the game-over flow; Claude picking one as a
        // story beat would break that pacing. Map-targeted scripted quests (Beggars, Pilgrims,
        // Mechanitor complex) are deliberately NOT here — doubling those is harmless flavour.
        private static readonly HashSet<string> ExcludedDefNames = new HashSet<string>
        {
            "GiveQuest_EndGame_ShipEscape",
            "GiveQuest_EndGame_ArchonexusVictory",
            "GiveQuest_EndGame_RoyalAscent",
            "GiveQuest_Intro_Wimp",
            "GiveQuest_Intro_Deserter",
            "GameEndedWanderersJoin"
        };

        // Discovery-only. ThreatBig is RimWorld's own "this is a big deal" marker, and the
        // difficulty gate in ClaudeStorytellerComp needs mod raids to trip it too.
        private static readonly HashSet<string> MajorThreatEventSet = new HashSet<string>();

        private static void EnsureDiscovered()
        {
            if (_discovered) return;

            var allDefs = DefDatabase<IncidentDef>.AllDefsListForReading;

            // If anything touches this type before defs finish loading, the sweep would find
            // nothing and latch an empty result permanently. Leave _discovered false and let
            // a later caller retry.
            if (allDefs == null || allDefs.Count == 0) return;

            _discovered = true;

            var seeded = new HashSet<string>(AllKnownEvents);
            var seedFound = new HashSet<string>();
            var perMod = new Dictionary<string, int>();
            var skipped = new List<string>();
            int added = 0;

            foreach (var def in allDefs)
            {
                if (def == null || def.defName == null) continue;
                if (ExcludedDefNames.Contains(def.defName))
                {
                    skipped.Add(def.defName + " (scripted)");
                    continue;
                }

                string modName = "unknown";
                try { modName = def.modContentPack?.Name ?? "unknown"; } catch { }

                if (seeded.Contains(def.defName))
                {
                    seedFound.Add(def.defName);
                    perMod[modName] = perMod.TryGetValue(modName, out int seedCount) ? seedCount + 1 : 1;
                    continue;
                }
                if (def.category == null) { skipped.Add(def.defName + " (no category)"); continue; }
                if (ExcludedCategories.Contains(def.category.defName))
                {
                    skipped.Add(def.defName + " (" + def.category.defName + ")");
                    continue;
                }

                // A def whose worker won't construct is unusable; skip it quietly rather
                // than letting it throw once per call inside GetAvailableEvents.
                try
                {
                    if (def.Worker == null) { skipped.Add(def.defName + " (no worker)"); continue; }
                }
                catch (Exception e)
                {
                    skipped.Add(def.defName + " (worker threw " + e.GetType().Name + ")");
                    continue;
                }

                switch (def.category.defName)
                {
                    case "DiseaseHuman":
                    case "DiseaseAnimal":
                        DiseaseEvents.Add(def.defName);
                        break;
                    case "ThreatBig":
                    case "ThreatSmall":
                    case "DeepDrillInfestation":
                        ThreatEventSet.Add(def.defName);
                        if (def.category.defName == "ThreatBig")
                            MajorThreatEventSet.Add(def.defName);
                        break;
                    case "FactionArrival":
                    case "OrbitalVisitor":
                    case "AllyAssistance":
                    case "GiveQuest":
                    case "ShipChunkDrop":
                        PositiveEvents.Add(def.defName);
                        break;
                    default:
                        // Misc, Special, and anything a mod invents. A def that installs a
                        // GameCondition is ambient pressure — that is what "weather" means
                        // to Claude here. The rest falls through to "other".
                        if (def.gameCondition != null)
                            WeatherEvents.Add(def.defName);
                        break;
                }

                AllKnownEvents.Add(def.defName);
                perMod[modName] = perMod.TryGetValue(modName, out int modCount) ? modCount + 1 : 1;

                string label = null;
                try { label = def.LabelCap; } catch { }
                if (!string.IsNullOrEmpty(label) && label != def.defName)
                    DiscoveredLabels[def.defName] = label;

                added++;
            }

            // Curated names that are not in this install (a DLC the player lacks, a renamed def).
            // They stay in AllKnownEvents and are filtered by GetNamedSilentFail at call time,
            // but they must not be mistaken for real pool members when reading the count.
            var seedMissing = seeded.Where(s => !seedFound.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            string perModText = string.Join(", ",
                perMod.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                      .Select(kv => kv.Key + " " + kv.Value));

            ClaudeLogger.LogEntry("EVENT_POOL_DISCOVERED",
                "DefDatabase: " + allDefs.Count + " IncidentDefs. Pool: " + (seedFound.Count + added) +
                " real (" + seedFound.Count + " seed present + " + added + " discovered); " +
                seedMissing.Count + " seed names missing from this install" +
                (seedMissing.Count > 0 ? " [" + string.Join(", ", seedMissing) + "]" : "") + "; " +
                skipped.Count + " skipped" +
                (skipped.Count > 0 ? " [" + string.Join(", ", skipped) + "]" : "") + "." +
                Environment.NewLine + "By mod: " + perModText);

            ClaudeLogger.LogEntry("EVENT_POOL_LIST", BuildPoolListText());
        }

        /// <summary>
        /// Every real pool member, grouped the same way Claude will see them, sorted, one bucket
        /// per line. Logged once at startup so "what events does the storyteller actually have"
        /// is answerable with grep instead of by opening a STATE_SENT payload.
        /// </summary>
        private static string BuildPoolListText()
        {
            var buckets = new Dictionary<string, List<string>>
            {
                { "weather", new List<string>() },
                { "threats", new List<string>() },
                { "positive", new List<string>() },
                { "disease", new List<string>() },
                { "other", new List<string>() }
            };

            foreach (var evt in AllKnownEvents)
            {
                if (DefDatabase<IncidentDef>.GetNamedSilentFail(evt) == null) continue;
                buckets[BucketOf(evt)].Add(evt);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var kv in buckets)
            {
                kv.Value.Sort(StringComparer.Ordinal);
                sb.Append(kv.Key).Append(" (").Append(kv.Value.Count).Append("): ")
                  .Append(string.Join(", ", kv.Value)).Append(Environment.NewLine);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The single source of truth for which bucket a defName lands in. Precedence matters:
        /// a seed name can sit in more than one set, and Claude must see it in exactly one list.
        /// </summary>
        private static string BucketOf(string evt)
        {
            if (WeatherEvents.Contains(evt)) return "weather";
            if (ThreatEventSet.Contains(evt)) return "threats";
            if (PositiveEvents.Contains(evt)) return "positive";
            if (DiseaseEvents.Contains(evt)) return "disease";
            return "other";
        }

        /// <summary>
        /// defName -> label for the subset of <paramref name="available"/> that came from
        /// runtime discovery rather than the curated seed. Sent to Claude so an unfamiliar
        /// mod defName is a choice rather than a coin flip.
        /// </summary>
        public static Dictionary<string, string> GetEventGlossary(List<string> available)
        {
            var glossary = new Dictionary<string, string>();
            if (available == null) return glossary;

            foreach (var evt in available)
            {
                if (DiscoveredLabels.TryGetValue(evt, out string label))
                    glossary[evt] = label;
            }
            return glossary;
        }

        // ========== Delegate to GameComponent ==========

        public static void RecordEvent(string type, string outcome, string requestedType, string source)
        {
            EnsureDiscovered();

            var comp = StorytellerGameComponent.Get();
            comp?.RecordEvent(type, outcome, requestedType, source);

            // Track disease separately for hard cooldown
            if (DiseaseEvents.Contains(type))
                comp?.RecordDiseaseFired();
        }

        public static void RecordColonistDeath(string name)
        {
            var comp = StorytellerGameComponent.Get();
            comp?.RecordColonistDeath(name);
        }

        public static void RecordColonistDowned(string name)
        {
            var comp = StorytellerGameComponent.Get();
            comp?.RecordColonistDowned(name);
        }

        public static bool IsWeatherEvent(string eventType)
        {
            EnsureDiscovered();
            return WeatherEvents.Contains(eventType);
        }

        public static bool IsThreatEvent(string eventType)
        {
            EnsureDiscovered();
            return ThreatEventSet.Contains(eventType);
        }

        /// <summary>
        /// True only for mod-added ThreatBig incidents. The curated vanilla major-threat
        /// list still lives in ClaudeStorytellerComp; this supplements it.
        /// </summary>
        public static bool IsMajorThreatEvent(string eventType)
        {
            EnsureDiscovered();
            return MajorThreatEventSet.Contains(eventType);
        }

        public static bool IsDiseaseEvent(string eventType)
        {
            EnsureDiscovered();
            return DiseaseEvents.Contains(eventType);
        }

        /// <summary>
        /// Returns true if a disease event is allowed (cooldown has elapsed).
        /// Hard minimum of 25 days between any two disease events.
        /// </summary>
        public static bool CanFireDisease()
        {
            var comp = StorytellerGameComponent.Get();
            if (comp == null) return true;

            int daysSinceDisease = comp.DaysSinceLastDisease;
            return daysSinceDisease >= 25;
        }

        // ========== Phase/State (public for GameComponent access) ==========

        public static string GetCurrentPhase()
        {
            if (Find.CurrentMap == null) return "early";
            int days = GenDate.DaysPassed;
            float wealth = Find.CurrentMap.wealthWatcher.WealthTotal;
            int colonists = Find.CurrentMap.mapPawns.FreeColonists.Count();
            return DeterminePhase(days, wealth, colonists);
        }

        public static string GetCurrentNarrativeState()
        {
            if (Find.CurrentMap == null) return "stable";
            int colonists = Find.CurrentMap.mapPawns.FreeColonists.Count();
            return DetermineNarrativeState(GenDate.DaysPassed, colonists);
        }

        // ========== Available Events ==========

        public static List<string> GetAvailableEvents(Map map)
        {
            EnsureDiscovered();

            var available = new List<string>();

            var eventsToCheck = new List<string>(AllKnownEvents);

            foreach (var eventName in eventsToCheck)
            {
                var def = DefDatabase<IncidentDef>.GetNamedSilentFail(eventName);
                if (def == null) continue;

                try
                {
                    // Caravan-only and Map_Dummy defs resolve to null and are never offered.
                    var target = ResolveTarget(def, map);
                    if (target == null) continue;

                    var parms = StorytellerUtility.DefaultParmsNow(def.category, target);
                    if (def.Worker.CanFireNow(parms))
                    {
                        available.Add(eventName);
                    }
                }
                catch
                {
                    // Skip events that error on CanFireNow check
                }
            }

            return available;
        }

        /// <summary>
        /// The incident target a def can legally fire against from the player's map pass: the
        /// map itself when its tags allow it, otherwise the World for world-scale incidents
        /// (Eclipse, SolarFlare, Aurora, GiveQuest_Random, mod world conditions), otherwise null.
        /// IncidentWorker.CanFireNow fails def.TargetAllowed(target) before anything else, so
        /// checking a World-only def against a Map — the pre-2026-09-07 behaviour — silently hid
        /// 46 incidents in this modlist. Used by both GetAvailableEvents and TryConvertToIncident
        /// so what is offered and what is fired agree.
        /// </summary>
        public static IIncidentTarget ResolveTarget(IncidentDef def, Map map)
        {
            if (def == null) return null;

            try
            {
                if (map != null && def.TargetAllowed(map)) return map;

                var world = Find.World;
                if (world != null && def.TargetAllowed(world)) return world;
            }
            catch
            {
                // A def with malformed targetTags is unusable; treat as no target.
            }

            return null;
        }

        public static Dictionary<string, List<string>> GetAvailableEventsByCategory(Map map)
        {
            var available = GetAvailableEvents(map);
            var categorized = new Dictionary<string, List<string>>
            {
                { "weather", new List<string>() },
                { "threats", new List<string>() },
                { "positive", new List<string>() },
                { "disease", new List<string>() },
                { "other", new List<string>() }
            };

            foreach (var evt in available)
                categorized[BucketOf(evt)].Add(evt);

            // Remove diseases entirely if cooldown hasn't elapsed
            if (!CanFireDisease())
            {
                categorized["disease"].Clear();
            }

            // Shuffle each category to prevent LLM position bias
            var rng = new Random(GenTicks.TicksGame);
            foreach (var key in categorized.Keys.ToList())
            {
                var list = categorized[key];
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    var temp = list[i];
                    list[i] = list[j];
                    list[j] = temp;
                }
            }

            return categorized;
        }

        // ========== Variance: Highlighted Events ==========

        /// <summary>
        /// Picks 3-5 random events from the available pool and suggests them to Claude.
        /// Different highlights each call breaks the LLM's tendency to pick the same "safe" events.
        /// </summary>
        public static List<string> GenerateHighlightedEvents(Dictionary<string, List<string>> availableByCategory)
        {
            var allAvailable = new List<string>();
            foreach (var kvp in availableByCategory)
                allAvailable.AddRange(kvp.Value);

            if (allAvailable.Count == 0) return new List<string>();

            var rng = new Random(GenTicks.TicksGame + 7919); // different seed from shuffle
            int count = Math.Min(rng.Next(3, 6), allAvailable.Count);

            var highlighted = new List<string>();
            var pool = new List<string>(allAvailable);

            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                int idx = rng.Next(pool.Count);
                highlighted.Add(pool[idx]);
                pool.RemoveAt(idx);
            }

            return highlighted;
        }

        // ========== Variance: Random Exclusion List ==========

        /// <summary>
        /// Randomly removes 2-3 events from the available pool each call.
        /// Forces Claude to pick from a different subset every time.
        /// Returns the excluded event names so Claude knows they're unavailable.
        /// Also removes them from the availableByCategory dict in place.
        /// </summary>
        public static List<string> GenerateExclusionList(Dictionary<string, List<string>> availableByCategory)
        {
            var allAvailable = new List<string>();
            foreach (var kvp in availableByCategory)
                allAvailable.AddRange(kvp.Value);

            if (allAvailable.Count <= 5) return new List<string>(); // don't exclude if pool is tiny

            var rng = new Random(GenTicks.TicksGame + 4217);
            int count = rng.Next(2, 4); // 2-3 exclusions
            count = Math.Min(count, allAvailable.Count / 3); // never exclude more than 1/3 of pool

            var excluded = new List<string>();
            var pool = new List<string>(allAvailable);

            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                int idx = rng.Next(pool.Count);
                string evt = pool[idx];
                excluded.Add(evt);
                pool.RemoveAt(idx);

                // Remove from the actual category dict
                foreach (var kvp in availableByCategory)
                {
                    kvp.Value.Remove(evt);
                }
            }

            return excluded;
        }

        // ========== Variance: Storytelling Mood ==========

        private static readonly string[] MoodThemes = new string[]
        {
            "isolation and the wild frontier",
            "calm before the storm",
            "nature's indifference to human ambition",
            "the fragility of civilization",
            "unexpected visitors and strange omens",
            "scarcity and resourcefulness",
            "hubris and overconfidence punished",
            "the bonds forged in hardship",
            "creeping dread from the horizon",
            "a season of strange weather",
            "the land itself turns hostile",
            "false security shattered",
            "whispers of distant threats",
            "abundance attracting predators",
            "mechanical menace in the deep",
            "pestilence carried on the wind",
            "the kindness of strangers",
            "fire and ruin",
            "a test of the colony's resolve",
            "the rhythm of survival breaking down",
            "paranoia after a long peace",
            "the wilderness reclaiming what was built",
            "desperate measures in desperate times",
            "an eerie quiet that begs to be broken"
        };

        /// <summary>
        /// Returns a random storytelling mood phrase to shift Claude's creative framing each call.
        /// </summary>
        public static string GenerateStorytellingMood()
        {
            var rng = new Random(GenTicks.TicksGame + 6271);
            return MoodThemes[rng.Next(MoodThemes.Length)];
        }

        // ========== Variance: Category Usage Tracking ==========

        /// <summary>
        /// Counts how many of the last N events came from each category.
        /// Tells Claude what it's been over/under-using.
        /// </summary>
        public static Dictionary<string, int> CollectCategoryUsage(StorytellerGameComponent comp, int count)
        {
            var usage = new Dictionary<string, int>
            {
                { "weather", 0 },
                { "threats", 0 },
                { "positive", 0 },
                { "disease", 0 },
                { "other", 0 }
            };

            if (comp == null) return usage;

            var recent = comp.GetRecentEventTypes(count);
            foreach (var evt in recent)
            {
                if (WeatherEvents.Contains(evt))
                    usage["weather"]++;
                else if (ThreatEventSet.Contains(evt))
                    usage["threats"]++;
                else if (PositiveEvents.Contains(evt))
                    usage["positive"]++;
                else if (DiseaseEvents.Contains(evt))
                    usage["disease"]++;
                else
                    usage["other"]++;
            }

            return usage;
        }

        // ========== Vanilla Reference (static, never changes) ==========

        private static VanillaReference _vanillaRef = null;

        public static VanillaReference GetVanillaReference()
        {
            if (_vanillaRef != null) return _vanillaRef;

            _vanillaRef = new VanillaReference
            {
                Cassandra = new VanillaStoryteller
                {
                    Style = "structured tension curve with on/off cycles",
                    MiscMtbDays = 3.0f,
                    ThreatCycleDays = 10.6f,
                    ThreatsPerCycle = "1-2",
                    RestPeriodDays = 6.0f,
                    MinThreatSpacingDays = 1.9f,
                    DiseaseApproxMtbDays = 18.0f
                },
                Phoebe = new VanillaStoryteller
                {
                    Style = "long peace periods with hard singular hits",
                    MiscMtbDays = 3.0f,
                    ThreatCycleDays = 16.0f,
                    ThreatsPerCycle = "1",
                    RestPeriodDays = 8.0f,
                    MinThreatSpacingDays = 12.5f,
                    DiseaseApproxMtbDays = 22.0f
                },
                Randy = new VanillaStoryteller
                {
                    Style = "pure weighted chaos, no schedule, no guaranteed rest",
                    MiscMtbDays = 1.13f,
                    ThreatCycleDays = 11.0f,
                    ThreatsPerCycle = "random",
                    RestPeriodDays = 0f,
                    MinThreatSpacingDays = 0f,
                    DiseaseApproxMtbDays = 15.0f
                }
            };

            return _vanillaRef;
        }

        // ========== Event Density ==========

        public static EventDensity CollectDensity(StorytellerGameComponent comp)
        {
            if (comp == null)
            {
                return new EventDensity
                {
                    EventsLast7Days = 0,
                    EventsLast15Days = 0,
                    ThreatsLast7Days = 0,
                    ThreatsLast15Days = 0,
                    DiseasesLast30Days = 0,
                    DaysSinceLastDisease = 999,
                    DaysSinceArcCompleted = 999,
                    ActiveArc = null,
                    QueuedArcBeats = 0
                };
            }

            var recentAll = comp.GetRecentEvents(30);
            int currentTick = Find.TickManager.TicksGame;

            int events7 = 0, events15 = 0;
            int threats7 = 0, threats15 = 0;
            int diseases30 = 0;

            foreach (var evt in recentAll)
            {
                if (evt.DaysAgo <= 7)
                {
                    events7++;
                    if (ThreatEventSet.Contains(evt.Type)) threats7++;
                }
                if (evt.DaysAgo <= 15)
                {
                    events15++;
                    if (ThreatEventSet.Contains(evt.Type)) threats15++;
                }
                if (evt.DaysAgo <= 30 && DiseaseEvents.Contains(evt.Type))
                {
                    diseases30++;
                }
            }

            return new EventDensity
            {
                EventsLast7Days = events7,
                EventsLast15Days = events15,
                ThreatsLast7Days = threats7,
                ThreatsLast15Days = threats15,
                DiseasesLast30Days = diseases30,
                DaysSinceLastDisease = comp.DaysSinceLastDisease,
                DaysSinceArcCompleted = comp.DaysSinceArcCompleted,
                ActiveArc = comp.ActiveArcName,
                QueuedArcBeats = comp.QueuedArcBeats
            };
        }

        // ========== Main State Collection ==========

        public static ColonyState CollectState(Map map, string callType)
        {
            if (map == null) return null;

            var comp = StorytellerGameComponent.Get();
            var colonists = map.mapPawns.FreeColonists.ToList();
            var wealth = map.wealthWatcher.WealthTotal;
            var days = GenDate.DaysPassed;

            var availableEvents = GetAvailableEventsByCategory(map);
            var availableQuests = GetAvailableQuests(map);

            // Per-call count, so the offered list is checkable without opening STATE_SENT.
            int realPool = AllKnownEvents.Count(e => DefDatabase<IncidentDef>.GetNamedSilentFail(e) != null);
            var offeredNames = availableEvents.SelectMany(kv => kv.Value).ToList();
            int worldTargeted = offeredNames.Count(e =>
                !(ResolveTarget(DefDatabase<IncidentDef>.GetNamedSilentFail(e), map) is Map));
            ClaudeLogger.LogEntry("AVAILABLE_EVENTS",
                string.Join(", ", availableEvents.Select(kv => kv.Key + " " + kv.Value.Count)) +
                " = " + offeredNames.Count + " of " + realPool +
                " pool events passed CanFireNow (" + worldTargeted + " world-targeted); " +
                availableQuests.Count + " quest(s) offered" +
                (availableQuests.Count > 0
                    ? " [" + string.Join(", ", availableQuests.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "]"
                    : "") +
                (CanFireDisease() ? "" : " (disease bucket cleared: cooldown)") + ".");

            var state = new ColonyState
            {
                RequestId = Guid.NewGuid().ToString("N").Substring(0, 8),
                CallType = callType,
                Colony = CollectColonyInfo(map, colonists, wealth, days),
                CombatReadiness = CollectCombatReadiness(map, colonists),
                Resources = CollectResources(map, colonists),
                RecentHistory = CollectRecentHistory(comp, map),
                Cooldowns = null,
                AvailableFactions = CollectFactions(),
                FactionDetails = CollectFactionDetails(),
                DoNotRepeat = comp?.GetRecentEventTypes(3) ?? new List<string>(),
                CurrentQueue = CollectQueueContext(),
                Difficulty = CollectDifficulty(),
                AvailableEvents = availableEvents,
                EventGlossary = GetEventGlossary(
                    availableEvents.SelectMany(kv => kv.Value).ToList()),
                Density = CollectDensity(comp),
                LastPosture = comp?.LastPosture ?? "none — first call",
                HighlightedEvents = GenerateHighlightedEvents(availableEvents),
                ExcludedThisCall = GenerateExclusionList(availableEvents),
                StorytellingMood = GenerateStorytellingMood(),
                CategoryUsageLast5 = CollectCategoryUsage(comp, 5),
                RandomSeed = Rand.Int,
                UninvitedIncidents = PawnEventPatch.GetUninvitedIncidents(),
                ArcProgress = comp?.BuildArcProgress(map),
                RecurringFactions = comp?.GetRecurringFactions() ?? new List<string>(),
                ColonistNames = colonists.OrderBy(p => p.thingIDNumber)
                    .Select(p => p.Name?.ToStringShort ?? p.LabelShortCap).ToList(),
                CastChangesSinceLastCall = comp?.GetCastChangesSinceLastCall(),
                AvailableQuests = availableQuests
            };

            // One call = one increment, so calls_since_beat_authored actually counts calls.
            if (comp != null && !string.IsNullOrEmpty(comp.ActiveArcName))
                comp.IncrementCallsSinceBeatAuthored();

            // Always include arc history for unified calls
            var allAvailable = GetAvailableEvents(map);
            var arcLog = comp?.GetArcLog() ?? new List<ArcLogEntry>();
            state.ArcHistory = ArcSummarizer.Summarize(arcLog, allAvailable);

            // Send-once semantics: clear after building the state we're about to send.
            PawnEventPatch.ClearUninvitedIncidents();
            comp?.ClearCastChangesSinceLastCall();

            return state;
        }

        public static DifficultyInfo CollectDifficulty()
        {
            var diff = Find.Storyteller.difficulty;
            float threatScale = diff.threatScale;

            float diffLevel = diff.threatScale;
            string label;
            float maxIntensity;
            float minIntensity;
            bool allowThreats;
            bool allowMajorThreats;

            if (diffLevel <= 0.1f)
            {
                label = "Peaceful";
                maxIntensity = 0f;
                minIntensity = 0f;
                allowThreats = false;
                allowMajorThreats = false;
            }
            else if (diffLevel <= 0.5f)
            {
                label = "Community Builder";
                maxIntensity = 0.6f;
                minIntensity = 0.2f;
                allowThreats = true;
                allowMajorThreats = false;
            }
            else if (diffLevel <= 0.8f)
            {
                label = "Adventure Story";
                maxIntensity = 1.0f;
                minIntensity = 0.3f;
                allowThreats = true;
                allowMajorThreats = true;
            }
            else if (diffLevel <= 1.2f)
            {
                label = "Strive to Survive";
                maxIntensity = 1.3f;
                minIntensity = 0.4f;
                allowThreats = true;
                allowMajorThreats = true;
            }
            else if (diffLevel <= 1.6f)
            {
                label = "Blood and Dust";
                maxIntensity = 1.5f;
                minIntensity = 0.5f;
                allowThreats = true;
                allowMajorThreats = true;
            }
            else if (diffLevel <= 2.0f)
            {
                label = "Losing is Fun";
                maxIntensity = 2.0f;
                minIntensity = 0.6f;
                allowThreats = true;
                allowMajorThreats = true;
            }
            else
            {
                label = "Custom";
                maxIntensity = Math.Max(0.5f, threatScale * 1.5f);
                minIntensity = Math.Max(0.2f, threatScale * 0.3f);
                allowThreats = threatScale > 0f;
                allowMajorThreats = threatScale > 0.3f;
            }

            return new DifficultyInfo
            {
                Label = label,
                ThreatScale = threatScale,
                MaxIntensity = maxIntensity,
                MinIntensity = minIntensity,
                AllowThreats = allowThreats,
                AllowMajorThreats = allowMajorThreats
            };
        }

        private static QueueContext CollectQueueContext()
        {
            return new QueueContext
            {
                PendingCount = EventQueue.Count,
                QueuedTypes = EventQueue.GetQueuedTypes(),
                QueueSummary = EventQueue.GetQueueSummary()
            };
        }

        private static ColonyInfo CollectColonyInfo(Map map, List<Pawn> colonists, float wealth, int days)
        {
            var phase = DeterminePhase(days, wealth, colonists.Count);
            var narrativeState = DetermineNarrativeState(days, colonists.Count);

            float raidPoints = StorytellerUtility.DefaultThreatPointsNow(map);
            float adaptation = Find.StoryWatcher.watcherAdaptation.AdaptDays;
            float threatScale = Find.Storyteller.difficulty.threatScale;

            return new ColonyInfo
            {
                Name = map.Parent.Label ?? "Colony",
                DaysSurvived = days,
                Phase = phase,
                NarrativeState = narrativeState,
                ColonistCount = colonists.Count,
                Wealth = wealth,
                RaidPoints = raidPoints,
                AdaptationScore = Math.Min(100, adaptation),
                ThreatScale = threatScale
            };
        }

        private static string DeterminePhase(int days, float wealth, int colonists)
        {
            if (days < 30 || wealth < 30000) return "early";
            if (days < 90 || wealth < 80000) return "establishing";
            if (days < 200 || wealth < 200000) return "mid-game";
            return "late-game";
        }

        private static string DetermineNarrativeState(int days, int colonists)
        {
            var comp = StorytellerGameComponent.Get();
            int lastDeathTick = comp?.LastDeathTick ?? -999999;

            int ticksSinceDeath = Find.TickManager.TicksGame - lastDeathTick;
            int daysSinceDeath = ticksSinceDeath / GenDate.TicksPerDay;

            float adaptation = Find.StoryWatcher.watcherAdaptation.AdaptDays;

            if (daysSinceDeath < 5) return "struggling";
            if (daysSinceDeath < 15 && adaptation < 30) return "recovering";
            if (adaptation > 80) return "snowballing";
            if (adaptation > 60) return "thriving";
            return "stable";
        }

        private static RecentHistory CollectRecentHistory(StorytellerGameComponent comp, Map map)
        {
            int lastThreatTick = comp?.LastThreatTick ?? -999999;
            int lastDeathTick = comp?.LastDeathTick ?? -999999;
            int lastDownedTick = comp?.LastDownedTick ?? -999999;

            int daysSinceThreat = lastThreatTick < 0
                ? 999 : (Find.TickManager.TicksGame - lastThreatTick) / GenDate.TicksPerDay;
            int daysSinceDeath = lastDeathTick < 0
                ? 999 : (Find.TickManager.TicksGame - lastDeathTick) / GenDate.TicksPerDay;
            int daysSinceDowned = lastDownedTick < 0
                ? 999 : (Find.TickManager.TicksGame - lastDownedTick) / GenDate.TicksPerDay;

            return new RecentHistory
            {
                DaysSinceThreat = Math.Max(0, daysSinceThreat),
                DaysSinceColonistDeath = Math.Max(0, daysSinceDeath),
                DaysSinceColonistDowned = Math.Max(0, daysSinceDowned),
                ThreatActiveNow = map != null && GenHostility.AnyHostileActiveThreatToPlayer(map),
                ColonistDeathsTotal = comp?.ColonistDeathsTotal ?? 0,
                ColonistDownedTotal = comp?.ColonistDownedTotal ?? 0,
                LastColonistDeathName = comp?.LastDeathName,
                LastEvents = comp?.GetRecentEvents(5) ?? new List<PastEvent>()
            };
        }

        private static CombatReadiness CollectCombatReadiness(Map map, List<Pawn> colonists)
        {
            var defenses = new List<string>();
            var vulnerabilities = new List<string>();

            int turretCount = map.listerBuildings.AllBuildingsColonistOfClass<Building_Turret>().Count();
            if (turretCount > 5) defenses.Add("turrets");

            bool hasPerimeter = map.listerBuildings.allBuildingsColonist
                .Any(b => b.def.building != null && b.def.fillPercent >= 1f);
            if (hasPerimeter) defenses.Add("perimeter_wall");

            int trapCount = map.listerBuildings.allBuildingsColonist
                .Count(b => b.def.building != null && b.def.building.isTrap);
            if (trapCount > 10) defenses.Add("trap_corridor");

            float avgMelee = 0f;
            float avgRanged = 0f;
            if (colonists.Count > 0)
            {
                avgMelee = (float)colonists.Average(p => p.skills.GetSkill(SkillDefOf.Melee).Level);
                avgRanged = (float)colonists.Average(p => p.skills.GetSkill(SkillDefOf.Shooting).Level);
            }

            string meleeStr = avgMelee < 6 ? "low" : avgMelee < 12 ? "medium" : "high";
            string rangedStr = avgRanged < 6 ? "low" : avgRanged < 12 ? "medium" : "high";

            bool hasWood = map.listerBuildings.allBuildingsColonist
                .Any(b => b.Stuff != null && b.Stuff.IsStuff && b.Stuff.stuffProps.categories.Any(c => c.defName == "Woody"));
            if (hasWood) vulnerabilities.Add("wooden_structures");

            if (turretCount == 0) vulnerabilities.Add("no_turrets");

            bool hasEmp = colonists.Any(p => p.equipment?.Primary?.def?.Verbs != null &&
                p.equipment.Primary.def.Verbs.Any(v => v.defaultProjectile?.projectile?.damageDef == DamageDefOf.EMP));
            if (!hasEmp) vulnerabilities.Add("no_emp");

            float score = 0.5f;
            score += turretCount * 0.02f;
            score += trapCount * 0.01f;
            score += (avgMelee + avgRanged) / 40f * 0.2f;
            score = Math.Min(1f, Math.Max(0f, score));

            return new CombatReadiness
            {
                Score = score,
                MeleeStrength = meleeStr,
                RangedStrength = rangedStr,
                Defenses = defenses,
                Vulnerabilities = vulnerabilities
            };
        }

        private static Resources CollectResources(Map map, List<Pawn> colonists)
        {
            int foodDays = CurrentFoodDays(map, colonists.Count);
            string medicine = MedicineTier(map);

            int compCount = map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial);
            string components = compCount < 5 ? "none" : compCount < 15 ? "low" : compCount < 40 ? "adequate" : "abundant";

            int silver = map.resourceCounter.GetCount(ThingDefOf.Silver);

            return new Resources
            {
                FoodDays = foodDays,
                Medicine = medicine,
                Components = components,
                Silver = silver
            };
        }

        /// <summary>Food days remaining for the current colonist count. Public so StartArc can capture a baseline.</summary>
        public static int CurrentFoodDays(Map map)
        {
            if (map == null) return 999;
            int colonistCount = map.mapPawns?.FreeColonists?.Count() ?? 0;
            return CurrentFoodDays(map, colonistCount);
        }

        private static int CurrentFoodDays(Map map, int colonistCount)
        {
            if (map == null) return 999;
            float totalNutrition = map.resourceCounter.TotalHumanEdibleNutrition;
            float dailyNeed = colonistCount * 1.6f;
            return dailyNeed > 0 ? (int)(totalNutrition / dailyNeed) : 999;
        }

        /// <summary>Medicine tier label ("none"/"low"/"adequate"/"abundant"). Public so StartArc can capture a baseline.</summary>
        public static string MedicineTier(Map map)
        {
            if (map == null) return "none";
            int medCount = map.resourceCounter.GetCount(ThingDefOf.MedicineIndustrial) +
                          map.resourceCounter.GetCount(ThingDefOf.MedicineHerbal) +
                          map.resourceCounter.GetCount(ThingDefOf.MedicineUltratech) * 2;
            return medCount < 5 ? "none" : medCount < 15 ? "low" : medCount < 40 ? "adequate" : "abundant";
        }

        /// <summary>
        /// Real Faction.Names for every non-hidden, non-defeated, non-player, non-temporary
        /// faction — hostile AND friendly, no more Tribal/Pirate collapse. Mechanoid factions
        /// are Hidden (ResolveFaction's exact-name match skips Hidden, same as here) and so
        /// never contribute a usable name; the literal "Mechanoid" is added back as a synthetic
        /// entry whenever a live hostile mechanoid faction exists, so ResolveFaction's kind
        /// fallback still has something to match.
        /// </summary>
        private static List<string> CollectFactions()
        {
            var factions = new List<string>();

            foreach (var faction in Find.FactionManager.AllFactions)
            {
                if (faction.Hidden || faction.defeated || faction == Faction.OfPlayer || faction.temporary) continue;
                if (string.IsNullOrEmpty(faction.Name)) continue;
                factions.Add(faction.Name);
            }

            bool hasHostileMechanoid = Find.FactionManager.AllFactions.Any(f =>
                f.def == FactionDefOf.Mechanoid && !f.defeated && f.HostileTo(Faction.OfPlayer));
            if (hasHostileMechanoid) factions.Add("Mechanoid");

            return factions.Distinct().ToList();
        }

        /// <summary>
        /// name -> "hostile, goodwill -100, tribal" style summary for every faction CollectFactions
        /// would name (same filter; the synthetic "Mechanoid" entry is skipped here since it has
        /// no real Faction to read goodwill from). Lets Claude reason about allies/neutrals, not
        /// just who to send a raid from.
        /// </summary>
        private static Dictionary<string, string> CollectFactionDetails()
        {
            var details = new Dictionary<string, string>();

            foreach (var faction in Find.FactionManager.AllFactions)
            {
                if (faction.Hidden || faction.defeated || faction == Faction.OfPlayer || faction.temporary) continue;
                if (string.IsNullOrEmpty(faction.Name)) continue;

                string relation = faction.HostileTo(Faction.OfPlayer) ? "hostile"
                    : faction.PlayerRelationKind == FactionRelationKind.Ally ? "ally"
                    : "neutral";
                string kind = faction.def == FactionDefOf.Pirate ? "pirate"
                    : faction.def.techLevel <= TechLevel.Neolithic ? "tribal"
                    : "outlander";

                details[faction.Name] = relation + ", goodwill " + faction.PlayerGoodwill + ", " + kind;
            }

            return details;
        }

        // ========== Named quests (item 4 of the roadmap) ==========
        // Hand-written glosses for the known vanilla/DLC root scripts (RESEARCH_EVENT_SOURCES.md
        // "Tier 2"). Payload-side text only — Claude reads these, the player never does — so they
        // stay short and mechanical rather than atmospheric. Unknown/mod scripts fall back to a
        // prettified defName rather than being silently dropped.
        private static readonly Dictionary<string, string> QuestGlossary = new Dictionary<string, string>
        {
            // Core
            { "OpportunitySite_BanditCamp", "map site with a bandit camp to raid or bypass" },
            { "OpportunitySite_DownedRefugee", "map site with a downed refugee to rescue" },
            { "OpportunitySite_ItemStash", "map site with an item stash to claim" },
            { "OpportunitySite_PeaceTalks", "map site to negotiate peace with a faction" },
            { "OpportunitySite_PrisonerWillingToJoin", "map site with a prisoner willing to join" },
            { "ThreatReward_Raid_Joiner", "accept a raid now in exchange for a joiner" },
            { "TradeRequest", "a faction requests specific goods for payment" },
            // Royalty
            { "Hospitality_Joiners", "guests staying who may join the colony after" },
            { "Hospitality_Refugee", "a refugee seeking shelter, may join after" },
            { "Hospitality_Prisoners", "prisoners delivered for the colony to host or free" },
            { "Hospitality_Animals", "animals delivered for the colony to host or tame" },
            { "Mission_BanditCamp", "a bandit camp mission with a chosen reward" },
            { "PawnLend", "a faction asks to borrow a colonist temporarily" },
            { "ShuttleCrash_Rescue", "a crashed shuttle with survivors to rescue" },
            // Ideology
            { "OpportunitySite_AncientComplex", "map site with an ancient complex to explore" },
            // Biotech
            { "PollutionDump", "a faction asks to dump pollution near the colony" },
            { "SanguophageMeetingHost", "a sanguophage asks to meet, possibly to join" },
            { "SanguophageShip", "a sanguophage ship requests contact or trade" },
            // Odyssey
            { "GravshipWreckage", "a wrecked gravship with salvage to recover" },
            { "OpportunitySite_Asteroid", "map site on an asteroid with salvage" },
            { "OpportunitySite_Satellite", "map site on a satellite with salvage" },
            { "OpportunitySite_OrbitalItemStash", "orbital site with an item stash to claim" },
            { "OpportunitySite_OrbitalWreck", "orbital wreck site with salvage to recover" },
            { "OrbitalFugitive", "a fugitive in orbit seeking rescue or capture" },
            { "SurveySite", "a site to survey for resources or hazards" },
            { "OpportunitySite_AncientMercenaries", "map site with ancient mercenaries to recruit or fight" },
            { "OpportunitySite_AbandonedPlatform", "map site on an abandoned platform to explore" }
        };

        private static string GlossForQuest(string defName)
        {
            if (QuestGlossary.TryGetValue(defName, out string gloss)) return gloss;
            return PrettifyDefName(defName).ToLowerInvariant();
        }

        /// <summary>Splits "Some_DefName" / "SomeDefName" into "Some Def Name" for the fallback gloss.</summary>
        private static string PrettifyDefName(string defName)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < defName.Length; i++)
            {
                char c = defName[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(defName[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// defName -> gloss for every root-selectable (rootSelectionWeight > 0, i.e.
        /// IsRootRandomSelected) QuestScriptDef that CanRun right now. Called per storyteller
        /// call, NEVER from the static ctor: CanRun needs a live game (Find.World, storyteller
        /// threat points), which does not exist at startup. Util_* subroutines and fixed
        /// endgame/intro scripts are excluded automatically — they are not root-random-selected.
        /// </summary>
        public static Dictionary<string, string> GetAvailableQuests(Map map)
        {
            var quests = new Dictionary<string, string>();
            if (map == null || Find.World == null) return quests;

            float points;
            try { points = StorytellerUtility.DefaultThreatPointsNow(Find.World); }
            catch { return quests; }

            foreach (var script in DefDatabase<QuestScriptDef>.AllDefsListForReading)
            {
                if (script == null || !script.IsRootRandomSelected) continue;

                bool canRun;
                try { canRun = script.CanRun(points, Find.World); }
                catch (Exception e)
                {
                    ClaudeLogger.LogEntry("QUEST_SCRIPT_SKIPPED",
                        script.defName + " CanRun threw " + e.GetType().Name);
                    continue;
                }
                if (!canRun) continue;

                quests[script.defName] = GlossForQuest(script.defName);
            }

            return quests;
        }
    }
}
