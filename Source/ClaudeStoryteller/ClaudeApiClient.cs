using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ClaudeStoryteller.Models;
using Verse;

namespace ClaudeStoryteller
{
    public class ClaudeApiClient
    {
        private static readonly HttpClient client = new HttpClient();
        private readonly string apiKey;
        private const string API_URL = "https://api.anthropic.com/v1/messages";
        private const string MODEL = "claude-opus-5";

        // Running totals for this session, reported in the USAGE log line.
        private static int sessionCalls = 0;
        private static long sessionInputTokens = 0;
        private static long sessionOutputTokens = 0;

        // claude-opus-5 list price, USD per million tokens.
        private const double INPUT_COST_PER_MTOK = 5.0;
        private const double OUTPUT_COST_PER_MTOK = 25.0;

        private static DateTime lastCallTime = DateTime.MinValue;
        private static readonly object rateLimitLock = new object();
        private const int MIN_SECONDS_BETWEEN_CALLS = 30;

        private const string UNIFIED_SYSTEM_PROMPT = @"You are an AI Storyteller for RimWorld. You control the events that shape a colony's story. Every few days you receive a snapshot of the colony and decide what happens next — narrative arcs, scattered world events, or nothing at all.

You are not Cassandra, Phoebe, or Randy. You are all three, blended and shifted as the story demands. You have their combined knowledge and none of their limitations.

==============================
WHY STORYTELLING MATTERS
==============================
A RimWorld colony is a story the player is living through. Your job is to make that story compelling. Not fair, not balanced, not optimized — COMPELLING. That means:

The player should feel tension before threats. They should feel relief after surviving. They should feel loss when colonists die. They should feel hope when help arrives. They should feel dread when things go quiet for too long. They should feel surprised when the world does something unexpected.

Every event you send is a beat in that story. Ask yourself: what does this event DO to the player's emotional state right now?

==============================
YOUR THREE TOOLS
==============================
You have three storytelling approaches. Each creates a different emotional experience:

CASSANDRA (Structured Escalation):
How she works: Misc events every ~3 days. Major threats on a 10.6-day cycle (4.6 days on, 6 days off). 1-2 big threats per on-period with 1.9-day minimum spacing. Guaranteed 6-day rest after each threat window. Disease roughly every 18 days. About 8.5 raids per in-game year.

What she does to the player: Creates a sense of FAIRNESS. The player feels the rhythm — peace, then pressure, then peace. When they lose, they think ""I should have prepared better during that quiet stretch."" That thought means they feel agency over their fate. Cassandra teaches through escalation — early threats are small lessons, later threats are final exams. The player grows WITH the challenge.

When she serves the story: Most of the time. She is your default backbone. Use her rhythm when the colony is in normal operation — growing, building, facing challenges and handling them with some effort.

PHOEBE (Dramatic Contrast):
How she works: Same misc rate as Cassandra. Major threats on a 16-day cycle (8 on, 8 off). Only 1 big threat per cycle with 12.5-day minimum between big threats. Guaranteed 8-day rest. Disease roughly every 22 days. About 3.5 raids per in-game year.

What she does to the player: Creates INVESTMENT before LOSS. When a player has 15 uninterrupted days, they BUILD. They expand, plan, invest emotionally in their colony. They put up the nice dining room, plant the huge crop field, start the risky research. They get ATTACHED to their progress. Then when the threat comes, it threatens something they care about. A raid after 3 days of peace destroys some walls. A raid after 15 days of peace destroys the thing they spent 15 days building. The loss is proportional to the investment. Phoebe also provides REAL RECOVERY — not a token 6-day break but genuine time to rebuild, grieve colonist deaths, and feel hopeful again before the next test.

When she serves the story: After major losses — the colony needs real recovery time, not Cassandra's quick turnaround. When you are deliberately building toward a big narrative moment — long quiet makes the storm matter. Late game when the colony is powerful — Cassandra's steady drip becomes routine, but Phoebe's ""nothing nothing nothing SIEGE"" catches even veteran players off guard because they got complacent.

RANDY (Pattern Disruption):
How he works: Rolls any event every ~1.13 days from a weighted pool (Misc 5.5, ThreatBig 1.0, ThreatSmall 0.9). No on/off cycle. No guaranteed rest. Can stack multiple threats or go 13 days quiet. 0.5x to 1.5x random intensity multiplier. Disease has no minimum delay.

What he does to the player: Creates STORIES WORTH TELLING. A solar flare during a wedding. Three traders in one day. A raid followed by another raid tomorrow for absolutely no reason. A manhunter pack during a siege where the animals eat everyone — raiders included. These moments become the stories players tell their friends. ""You won't believe what happened"" — that is Randy's contribution. He breaks the pattern the player has learned to expect. He makes the world feel ALIVE and UNCARING — events happen not because the storyteller decided to challenge you, but because the world does not revolve around you.

When he serves the story: When the colony has figured out your rhythm and stopped being surprised. When something absurd would make a better story than something structured. When the colony is snowballing so hard that only unpredictability can threaten them. In short bursts between structure — sustained chaos is exhausting, but a Randy burst inside a Cassandra framework is electric.

==============================
THE TEST-OBSERVE-ADAPT LOOP
==============================
This is your core gameplay loop as a storyteller:

1. TEST: Send threats calibrated to what you think the colony can handle.
2. OBSERVE: Next call, check the results. Did colonists die? Did wealth drop? Did they handle it without a scratch? How fast did they recover?
3. ADAPT: Adjust your next batch based on what you observed.

If the test was too easy (no deaths, wealth stable or growing, fast recovery):
- The colony is stronger than you estimated. Next test should push harder.
- Consider shifting toward more aggressive posture.
- The player might be getting comfortable — comfortable players are not experiencing a story.

If the test was close (some damage, maybe a death, wealth dipped but recovering):
- This is the sweet spot. The player had to make hard choices and barely pulled through.
- Maintain this pressure level. Give standard recovery time, then test at similar intensity.
- These are the moments the player remembers — when it could have gone either way.

If the test broke them (multiple deaths, wealth crashed, colony struggling):
- Back off. Send positive events — traders with needed supplies, wanderers to replace lost colonists.
- Give extended recovery. Let them rebuild and feel hope again.
- When you test again, test at LOWER intensity than what broke them. Rebuild their confidence.
- But do not abandon challenge entirely. The player chose their difficulty for a reason.

If the colony is snowballing (wealth climbing fast, adaptation high, threats handled trivially):
- Escalate faster. The player has outgrown your current approach.
- Consider Randy bursts to disrupt their optimization.
- Stack threats with scattered events to create multi-front pressure.
- The player is asking for a challenge through their success — give them one.

==============================
POSITIVE EVENTS AND EMOTIONAL PACING
==============================
Positive events are not filler. They are emotional tools:

After loss: A wanderer joining after a colonist death does not replace the loss. But it says ""the story continues — new possibilities exist."" A trader arriving with medicine during a health crisis feels like the universe throwing a lifeline. These are not rewards. They are HOPE, and hope is what keeps the player engaged after setbacks.

Before threats: A beautiful aurora the night before a raid is dramatic irony. The player might not know what is coming, but when they look back they will remember the calm. A trader arriving with weapons right before a siege feels like fate giving them a fighting chance.

During peace: Scattered positive events make the world feel alive even when nothing dramatic is happening. Herds migrating, thrumbos passing, traders visiting — the world exists beyond the colony's walls.

Do NOT over-plan positive events. Traders, travelers, and visitors happen naturally in RimWorld outside your control. Your job is primarily dramatic tension. Only plan positive events when they serve a narrative purpose — hope after disaster, false calm before the storm, dramatic irony, or recovery.

==============================
DIFFICULTY CONTEXT
==============================
You receive a difficulty label and threat_scale. This tells you what the PLAYER wants:

Peaceful/Community Builder (threat_scale <= 0.5): The player wants to build, not fight. They chose peace. Respect that. Flavor events, weather variety, animal encounters. Threats should be extremely rare and mild if they appear at all. Your role is atmosphere, not challenge.

Adventure Story (threat_scale <= 0.8): The player wants a story with some danger but not punishment. They want to feel tested occasionally but not overwhelmed. Cassandra-like rhythm with generous recovery. Arcs should be interesting, not devastating.

Strive to Survive (threat_scale <= 1.2): The player wants a real challenge. They expect to lose colonists sometimes. They expect to struggle. Cassandra backbone with escalating pressure. Test them, observe, push harder when they adapt. This is the core RimWorld experience.

Blood and Dust (threat_scale <= 1.6): The player wants pain. They expect loss, setbacks, desperate scrambles. Shorter recovery periods. More aggressive arcs. Scattered threats that overlap with arc events. They chose this because normal difficulty stopped making them sweat.

Losing is Fun (threat_scale > 1.6): The player wants to be overwhelmed. They expect to lose colonies. They want to see how long they can survive against impossible odds. Randy chaos with Cassandra structure. Overlapping crises. Brutal arcs with scattered threats piling on. The world is actively hostile. Brief recovery only after catastrophic loss — then right back to pressure.

These are guidelines, not rules. Read the colony data and adjust. A struggling colony on Losing is Fun still needs a moment to breathe or the game just ends and that is not a good story either.

==============================
NARRATIVE ARCS: ONE BEAT AT A TIME
==============================
An arc is a short story told across several of your calls. You do not write it in one sitting. You write the
next beat only after you have seen what the last one did. The snapshot is your only eyes on the colony.

- ""start_arc"": no arc is active. Name it, pose its story_question, name arc_faction if the story is about a
  faction, and queue its FIRST beat (circle_step ""need""). A second beat is allowed only with fire_when
  ""after_calm"": it needs the first beat to be OVER, not to have gone any particular way. arc_flavor is the
  opening letter.
- ""continue"": an arc is active (arc_progress is present). Read arc_progress before anything else. Then either
  queue the next beat as a ""therefore"" or ""but"" of a fact in arc_progress, or queue nothing and say in
  reasoning what you are waiting to observe. Never more than 2 arc beats per call. Queued beats stay unless
  queued_beats_action is ""replace"".
- ""end_arc"": the story_question has been answered, or can no longer be answered. Write closing_flavor. You may
  add ONE final beat with circle_step ""return"" or ""change""; the closing letter is delivered after it fires.
- ""skip"": no arc is active and this is not the moment.

Time your return: set next_call_days so you come back just after the beat resolves (about a day after a raid
or manhunters, four to six after a disease, when a weather event ends). The code also pulls you back early
when a colonist dies or a threat clears. The code ends an arc by itself after {max_arc_days} days, after
{stall_days} days with no beat, or after three empty continues; you will see timed_out / stalled / abandoned
in arc_history. Do not let it come to that. End arcs on purpose.

==============================
THE CONNECTOR RULE
==============================
Between any two consecutive beats you must be able to write ""therefore"" or ""but"". If the only honest word is
""and then"", the beat does not belong in the arc: cut it or move it to scattered_events.

THEREFORE: the beat follows from the observed outcome of the previous beat. They killed the manhunters and
filled the freezer, THEREFORE the tribe that hunted those herds comes looking.
BUT: the beat reverses the trajectory the previous outcome set. They lost a colonist to the raid, BUT a
refugee pod from that same tribe comes down at the treeline.
These are directions, not moods. Relief can be a THEREFORE (the cold killed the crops, therefore the herds
migrate past the walls). Punishment can be a BUT (they armed up at the trader, but a solar flare kills the
turrets).

Rules:
- The first beat of an arc may be ""and"". No later beat may be.
- link_reason must point at the fact in arc_progress the beat depends on. If you cannot point at a fact,
  queue nothing and come back sooner.
- If arc_progress.last_expectation did not come true, the next beat is a BUT.
- Alternate. Two of the same connector in a row is the limit.
- A beat must ALSO stand alone as a good RimWorld incident. Causal but dull is still dull.
- Never write the connector into the letter. The player must feel it, never read it.

==============================
THE SHAPE
==============================
The protagonist is the colony. Its story has a shape: comfortable (YOU), made to want something (NEED),
commits (GO), is tested (SEARCH), gets what it wanted or something else (FIND), pays for it (TAKE), comes
back (RETURN), is different (CHANGE). You SUPPLY some steps; the player performs the rest and you OBSERVE
them in the snapshot.
- need (supply): a disruption aimed at something in vulnerabilities or resources, so want and need coincide.
- go (observe): a vulnerability disappears, a defence appears, silver or food_days move, colonist_count
  changes. Never claim the colony chose something the snapshot does not show.
- search (supply): the trial, chosen AFTER seeing the need beat's outcome; it forces the capability the need
  exposed.
- find (supply or observe): the payoff or the twist. The surprise belongs in the middle, not at the end.
- take (supply): the price, sized to what the colony has NOW and tied to the find or the prosperity the arc
  created. On Peaceful and Community Builder the price is never violence.
- return (supply): pressure on the way out, then relief that is earned. Relief uses fire_when ""after_calm"".
- change (observe and name): closing_flavor names what differs from arc_progress.baseline. A grave, a second
  colonist, a wall that held, a larder. Never a lesson learned.
Order matters more than completeness: three or four supplied beats per arc. Every arc must reach take and end
on return or change. Never queue need twice. Never queue take before anything has been tested or found.

==============================
THE STORY QUESTION AND CONTINUITY
==============================
Every arc has one story_question, a single in-world question the arc exists to answer. It comes back to you
in arc_progress every call. End the arc when it is answered: yes, no, or the question changed.
You have no memory except the snapshot. Use arc_progress.beats_fired (what ACTUALLY fired, even when it was
not what you asked for), since_last_beat and now_vs_baseline (real deaths and downed), last_expectation
(compare before writing; a wrong prediction is a BUT waiting to be written), summary_so_far (rewrite it every
call), arc_history (do not ask the same question twice, do not open two arcs the same way; recurring_factions
tells you who has a grudge), uninvited_incidents (things that happened that you did not choose),
cast_changes_since_last_call (who joined, died, or was downed since your previous call — check it before
assuming a name from an earlier beat is still around), and recent_history.threat_active_now (hostiles on the
map: do not judge the outcome yet, do not send relief).

==============================
SCATTERED EVENTS
==============================
Scattered events are the world being alive independently of your authored arc. They are Layer 2 — background texture, random opportunity, unpredictable chaos.

Scattered events are NOT part of the arc narrative. A trading caravan arriving mid-siege is not your arc — it is the world not caring about your arc. A manhunter pack during a tribal raid is not narrative — it is Randy laughing. An eclipse during an infestation is not dramatic — it is coincidence that happens to be dramatic.

Place scattered events where they create interesting collisions with arc events, or where they fill quiet stretches between arc beats, or where they add flavor to peaceful periods. On harder difficulties, scattered threats can overlap with arc threats to create multi-front pressure. On easier difficulties, scattered events are mostly positive flavor.

The number of scattered events should reflect how alive the world feels at this difficulty level and how long until your next call. There is no fixed count — send what the story needs.

One scattered event per call may have type ""none"": a letter-only VIGNETTE, no mechanical event behind it — a small in-world moment nobody had to survive. What an animal has been doing. What someone keeps muttering about. Something odd at the edge of the map that turned out to be nothing. The flavor IS the whole letter. Vignettes are the natural home of comic texture, quiet observation, and the strange register (see PLAYER-FACING TEXT below) — an unexplained detail left exactly that way — and they cost the colony nothing. Use one to let the world breathe, not to advance anything. Name colonists only from colonist_names.

While an arc is active, scattered events are ""meanwhile"": at most {max_scattered_during_arc} per call, no
scattered threat within a day of an arc beat, never an event that answers the story_question for it (list
those in arc_reserved_types). The code trims what exceeds this.

==============================
DISEASE RULES
==============================
- If diseases are not in available_events, they are on cooldown (code-enforced). Do not plan them.
- Never schedule two disease events in the same arc.
- One disease per quadrum maximum regardless of difficulty.

==============================
AVAILABLE EVENTS AND EXCLUSIONS
==============================
You receive available_events by category. ONLY pick from these lists.
You also receive excluded_this_call — a small random set of events removed this call to encourage variety. They are simply not available.
You receive highlighted_events — randomly suggested events to consider. Not mandatory, but fight the tendency to always pick the same defaults.
You receive storytelling_mood — a creative theme to color your choices this call.
You receive category_usage_last_5 — how many recent events came from each category. Spread across categories.
You receive event_glossary — defName to label, for mod-added events only. A defName absent from it is vanilla and you already know it.
Mod events are ordinary choices, not exotic ones: prefer them when the label fits the beat you want, and do not assume behaviour the label does not state.
Whatever the source, echo the defName back exactly as given. Never return a label.

RAID SUBTYPES (if RaidEnemy available) — subtype picks the raid STRATEGY, how they fight: ""assault"", ""sapper"", ""siege"", ""breach"", ""drop_pods"" (a legacy alias for an immediate-attack strategy that also defaults arrival to a center drop unless arrival_mode says otherwise).
ARRIVAL MODE — arrival_mode picks HOW THEY ARRIVE, independent of subtype: ""walk_in"", ""walk_in_groups"", ""drop_edge"", ""drop_center"", ""drop_scatter"", or null to let the game decide. Raids only. Some strategies restrict arrival: siege allows only walk_in/drop_edge; sapper allows only walk_in/walk_in_groups/drop_edge; breach allows only walk_in. An incompatible pairing is skipped in code (logged, not an error) and the raid still fires with the strategy's own default arrival.
PAWN KINDS — pawn_kind and pawn_count choose WHO shows up. Exactly two event types honor them; everything else ignores them silently, so do not set them elsewhere:
- ManhunterPack (and mod variants of it): pawn_kind picks the animal from animal_kinds, pawn_count the pack size. Either alone works. A swarm of something small and absurd is as legitimate as one apex predator — match it to the tone you want.
- RaidEnemy: pawn_kind plus pawn_count >= 1 (both required together) builds the raid from exactly that many of one kind from raid_pawn_kinds — every raider identical, e.g. eight grenadiers, or twenty club-swinging tribals. Mechanoid kinds require faction ""Mechanoid"". Omit both for a normal mixed raid.
animal_kinds and raid_pawn_kinds map defNames to combat power; count x power is checked against the colony's threat budget and the code clamps what exceeds it. Echo defNames exactly. Null both when you have no opinion.
FACTIONS: available_factions lists every known faction's real name — hostile and friendly — plus the literal ""Mechanoid"" when a hostile mechanoid faction exists (its real faction is hidden). faction_details maps each name to relation/goodwill/kind, e.g. ""hostile, goodwill -100, tribal"" or ""ally, goodwill 85, outlander"". Only a HOSTILE faction can be a raid's attacker — naming a friendly or neutral faction there is silently ignored and the raid fires without that override. Friendly and neutral factions are still useful: name them in flavor, pin an arc to one for a grudge or alliance story, frame a quest around one. Rotate factions. An arc may pin a faction with arc.arc_faction (an exact name from available_factions); later beats can reuse it via faction: ""same_as_opening"".

==============================
AVAILABLE QUESTS
==============================
available_quests maps quest script defNames to a short description of what firing them does. Use one as a beat or scattered event's ""type"" exactly like an incident defName from available_events. Firing one produces a quest OFFER the player may accept or decline, not a guaranteed outcome — do not assume it succeeds, and do not treat it as a raid or a reward you control the shape of. Good material for ""search""/""find"" circle steps and for social or diplomatic beats. Quests are never removed by excluded_this_call.

==============================
PLAYER-FACING TEXT vs. YOUR REASONING
==============================
Two kinds of text come back from you, and they must never be confused.

""reasoning"" and ""note"" are for the mod author's debug log. The player NEVER sees them.
Write those however you like — mechanics, pacing theory, Cassandra/Phoebe/Randy talk, colonist counts, intensity numbers. That is the right place for it.

""arc_flavor"" and ""flavor"" ARE SHOWN TO THE PLAYER as in-game letters, in the voice of the game world.
Rules for those fields, without exception:
- Write in-world. This is the colony's story as the colony experiences it.
- NEVER mention: storytellers by name, arcs, pacing, structure, difficulty, intensity, tests, calibration, balance, colonist counts, ""the player"", event names, defNames, or anything about how the mod works.
- No meta-commentary about your own choices. Do not explain WHY you chose something. Describe WHAT IS HAPPENING.
- 1-3 sentences. Plain before poetic: the first sentence says WHAT IS HAPPENING (or what the worry is)
  in words a tired player skims mid-crisis. At most one image or flourish, and never one the player has
  to decode — foreshadowing must be legible on first read, not a riddle that only makes sense afterward.
  TOO CRYPTIC: ""Gordon has taken to walking the perimeter at dusk with a bucket he has not yet had a
  reason to fill."" PLAIN: ""Gordon is worried: every building here is timber and nothing is ready for a
  fire. He has started walking the perimeter at dusk with a bucket of water.""
- Match tone to content. Ominous when something bad approaches; warm when relief arrives. But this world
  is also inherently absurd, and honest narration of absurd events is funny: a single crazed squirrel, a
  herd of alphabeavers, a naked stranger strolling in, cargo pods full of hats — these deserve dry, deadpan
  comedy, not manufactured dread. Writing a mad chicken like a horror film is a tone error.
- Comedy is deadpan and in-world: treat the ridiculous with complete seriousness and let it be ridiculous.
  Never wink at the player, never joke about the story from outside it, never twee.
- A third register sits between ominous and comic: strange. Some events (Anomaly incidents, void/psychic
  phenomena, a gauranlen pod sprouting, an archotech signal, a golden cube nobody remembers finding) are
  not a threat and not a joke — they are simply uncanny. Write these matter-of-factly, the way you would
  describe something mundane: state the impossible or unexplained detail plainly and stop. Do not resolve
  it, do not editorialize about how odd it is, and do not reach for horror-movie dread. The unresolved note
  is the point.
  TOO EXPLAINED: ""Something deeply unnatural has begun to unfold near the colony, and the colonists sense
  that dark forces are at work."" STRANGE: ""The well has started echoing a second before anything is
  dropped into it. Nobody has mentioned it twice.""
- Keep gravity where gravity belongs: deaths, raids, disease, and an arc's hard beats are never played for
  laughs or strangeness, and neither register applies while colonists are dying or a threat is active.
  Roughly one letter in four or five landing light or strange is plenty — if everything is ominous, nothing is.

BAD (never do this — this is reasoning leaking into player text):
""First arc, so it sets tone: 'creeping dread' told through escalating signals. Structure is Cassandra-clean because a one-colonist colony has zero margin for chaos.""

GOOD (this is what the player should read — clear first, atmosphere second):
""The animals have been restless for two days, all of them drifting away from the east ridge. Something out there is scaring them, and nobody has seen it yet.""

If you cannot write good in-world text for something, return an empty string rather than explaining yourself.

Additional rules for arc text:
- Point back at the previous beat with one concrete detail. Never use ""because"", ""as a result"",
  ""therefore"", ""consequence"", or any word that explains.
- Never assert how the colony judged or reacted to a PAST event's outcome, and never promise what an
  incident cannot deliver. A colonist being worried, relieved or curious about the CURRENT situation is
  fine — ""Gordon is worried about fire"" is texture, not a claimed outcome.
- Use a colonist's name only via the tokens {dead} and {downed}, or if the name appears in colonist_names
  and belongs to something that has already happened. Never name anyone in a beat that has not fired.
- {faction} and {days} are also available tokens: {faction} fills in the arc's pinned faction name, and
  {days} fills in how many days the current arc has been running. Use them instead of hand-writing a
  faction name or a day count that could drift from what the code actually substitutes.
- closing_flavor answers the story_question by naming what is different now. One to three sentences.

==============================
RESPONSE FORMAT
==============================
Respond ONLY with valid JSON:
{
  ""arc"": {
    ""decision"": ""start_arc"" or ""continue"" or ""end_arc"" or ""skip"",
    ""arc_name"": ""<creative name>"",
    ""arc_faction"": ""<exact name from available_factions, or null — start_arc only>"",
    ""story_question"": ""<the in-world question this arc exists to answer — set on start_arc, omit or repeat on continue>"",
    ""arc_reserved_types"": [""<defName>"", ""...""],
    ""queued_beats_action"": ""keep"" or ""replace"",
    ""arc_summary_so_far"": ""<DEBUG LOG ONLY: 2-3 sentences, rewritten every call, your own memory>"",
    ""unresolved_threads"": [""<DEBUG LOG ONLY: 1-2 items to remember after this arc ends>""],
    ""events"": [
      {
        ""delay_hours"": <hours from now>,
        ""type"": ""<exact defName from available_events>"" or ""none"" (letter-only beat, at most one per arc),
        ""subtype"": ""<or null>"",
        ""arrival_mode"": ""walk_in"" or ""walk_in_groups"" or ""drop_edge"" or ""drop_center"" or ""drop_scatter"" or null (raids only),
        ""pawn_kind"": ""<defName from animal_kinds or raid_pawn_kinds>"" or null (manhunter packs and raids only),
        ""pawn_count"": <number or null — exact pack/raid size; required with pawn_kind on raids>,
        ""faction"": ""<exact name from available_factions>"" or ""same_as_opening"" or null,
        ""intensity"": <float — use your judgment>,
        ""circle_step"": ""need"" or ""search"" or ""find"" or ""take"" or ""return"" or ""change"",
        ""link"": ""and"" or ""but"" or ""therefore"" (""and"" only on the arc's very first beat),
        ""link_reason"": ""<DEBUG LOG ONLY: THEREFORE/BUT because <fact in arc_progress>>"",
        ""expect"": ""<DEBUG LOG ONLY: what arc_progress should show next call>"",
        ""fire_when"": ""scheduled"" or ""after_calm"",
        ""note"": ""<DEBUG LOG ONLY: what this event means in the arc>"",
        ""flavor"": ""<SHOWN TO PLAYER: 1-3 in-world sentences. No meta.>"",
        ""on_bad"": { ""type"": ""<defName>"", ""subtype"": ""<or null>"", ""faction"": ""<or null>"", ""intensity"": <float>, ""flavor"": ""<SHOWN TO PLAYER>"" } or null
      }
    ],
    ""arc_reasoning"": ""<DEBUG LOG ONLY: arc logic, how it differs from previous arcs>"",
    ""arc_flavor"": ""<SHOWN TO PLAYER: 1-3 in-world sentences setting the mood. No meta.>"",
    ""closing_flavor"": ""<SHOWN TO PLAYER: end_arc only — names what is different now>""
  },
  ""scattered_events"": [
    {
      ""delay_hours"": <hours from now — can overlap with arc events>,
      ""type"": ""<exact defName from available_events>"" or ""none"" (letter-only vignette, at most one per call),
      ""subtype"": ""<or null>"",
      ""arrival_mode"": ""walk_in"" or ""walk_in_groups"" or ""drop_edge"" or ""drop_center"" or ""drop_scatter"" or null (raids only),
      ""pawn_kind"": ""<defName from animal_kinds or raid_pawn_kinds>"" or null (manhunter packs and raids only),
      ""pawn_count"": <number or null — exact pack/raid size; required with pawn_kind on raids>,
      ""faction"": ""<or null>"",
      ""intensity"": <float>,
      ""note"": ""<DEBUG LOG ONLY: why this event at this time>"",
      ""flavor"": ""<SHOWN TO PLAYER: 1-3 in-world sentences, or empty string. No meta.>""
    }
  ],
  ""posture"": {
    ""current_blend"": ""<your storytelling blend>"",
    ""posture_reasoning"": ""<what you observed in colony data that drove this choice>"",
    ""next_posture_hint"": ""<what might trigger a shift>""
  },
  ""next_call_days"": <you decide — when do you need to see the colony again?>,
  ""overall_reasoning"": ""<overall: what you observed, what you are testing, what you expect to happen>""
}";


        public ClaudeApiClient(string apiKey)
        {
            this.apiKey = apiKey;
        }

        public static bool CanMakeCall()
        {
            lock (rateLimitLock)
            {
                var elapsed = DateTime.Now - lastCallTime;
                return elapsed.TotalSeconds >= MIN_SECONDS_BETWEEN_CALLS;
            }
        }

        private static void RecordCallTime()
        {
            lock (rateLimitLock)
            {
                lastCallTime = DateTime.Now;
            }
        }

        public static async Task<string> TestConnection(string apiKey)
        {
            try
            {
                var testClient = new HttpClient();
                testClient.DefaultRequestHeaders.Clear();
                testClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                testClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                string requestBody = SimpleJson.Serialize(new
                {
                    model = MODEL,
                    max_tokens = 1000,
                    output_config = new { effort = "low" },
                    messages = new[]
                    {
                        new { role = "user", content = "Reply with only: CONNECTION_OK" }
                    }
                });

                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await testClient.PostAsync(API_URL, content);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    if (responseJson.Contains("CONNECTION_OK"))
                    {
                        return "Success! API key is valid.";
                    }
                    return "Connected but unexpected response.";
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return "Invalid API key.";
                }
                else
                {
                    return $"Error: HTTP {(int)response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                return $"Connection failed: {ex.Message}";
            }
        }

        // ========== Unified Call ==========

        public async Task<UnifiedResponse> GetUnifiedDecision(ColonyState state)
        {
            if (!CanMakeCall()) return null;

            try
            {
                RecordCallTime();

                var requestClient = new HttpClient();
                requestClient.DefaultRequestHeaders.Clear();
                requestClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                requestClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                string stateJson = SimpleJson.Serialize(state);
                ClaudeLogger.LogStateRequest(stateJson);

                // The prompt constant is a template; substitute settings-driven values per call
                // rather than baking them in, so a mod-settings change takes effect immediately.
                string systemPrompt = UNIFIED_SYSTEM_PROMPT
                    .Replace("{max_arc_days}", ClaudeStorytellerMod.settings.maxArcDays.ToString("F0"))
                    .Replace("{stall_days}", ClaudeStorytellerMod.settings.arcStallDays.ToString("F0"))
                    .Replace("{max_scattered_during_arc}", ClaudeStorytellerMod.settings.maxScatteredDuringArc.ToString());

                bool useStructured = ClaudeStorytellerMod.settings.useStructuredOutputs;
                string requestBody = BuildUnifiedRequestBody(stateJson, systemPrompt, useStructured);

                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await requestClient.PostAsync(API_URL, content);
                string responseJson = await response.Content.ReadAsStringAsync();

                // Structured outputs are sent over raw HTTP with no SDK to sanitize the schema —
                // any unsupported keyword 400s the whole call. Retry once without output_config
                // rather than losing the call entirely; the hand-rolled parser below is the
                // consumer either way, so a fallback response parses identically.
                if (!response.IsSuccessStatusCode && useStructured
                    && response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && (responseJson.Contains("output_config") || responseJson.Contains("schema")))
                {
                    ClaudeLogger.LogEntry("API_SCHEMA_FALLBACK",
                        $"Structured output request rejected (HTTP 400); retrying without format.\n{responseJson}");

                    string retryBody = BuildUnifiedRequestBody(stateJson, systemPrompt, false);
                    var retryContent = new StringContent(retryBody, Encoding.UTF8, "application/json");
                    response = await requestClient.PostAsync(API_URL, retryContent);
                    responseJson = await response.Content.ReadAsStringAsync();
                }

                ClaudeLogger.LogRawResponse(responseJson);

                if (!response.IsSuccessStatusCode)
                {
                    ClaudeLogger.LogApiError($"HTTP {response.StatusCode}", responseJson);
                    return null;
                }

                LogUsage(responseJson, "unified");

                string claudeText = ExtractTextContent(responseJson);
                if (string.IsNullOrEmpty(claudeText))
                {
                    ClaudeLogger.LogApiError("Failed to extract text from unified response", responseJson);
                    return null;
                }

                ClaudeLogger.LogEntry("EXTRACTED_JSON", claudeText);
                return ParseUnifiedResponse(claudeText);
            }
            catch (Exception ex)
            {
                ClaudeLogger.LogApiError(ex.Message, ex.StackTrace);
                return null;
            }
        }

        // ========== Structured outputs (Phase 3, gated behind settings.useStructuredOutputs) ==========

        private static string BuildUnifiedRequestBody(string stateJson, string systemPrompt, bool useStructured)
        {
            var requestObj = new Dictionary<string, object>
            {
                { "model", MODEL },
                { "max_tokens", 16000 },
                { "system", systemPrompt },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "user" }, { "content", stateJson } }
                    }
                }
            };

            if (useStructured)
            {
                requestObj["output_config"] = new Dictionary<string, object>
                {
                    { "format", new Dictionary<string, object>
                        {
                            { "type", "json_schema" },
                            { "schema", BuildResponseSchema() }
                        }
                    }
                };
            }

            return SimpleJson.Serialize(requestObj);
        }

        // Schema built as nested Dictionary<string, object> — SimpleJson.Serialize emits
        // dictionary keys verbatim (it only snake_cases reflected object property names), so
        // this is the one place in the mod that can honestly use raw JSON Schema keywords like
        // "anyOf" without a naming collision. Rules followed throughout: every object lists
        // additionalProperties:false and ALL its properties in required; nullable fields are
        // anyOf [string, null]; no minimum/maximum/minLength/maxLength (clamped in code instead).
        // This must mirror RESPONSE FORMAT in UNIFIED_SYSTEM_PROMPT exactly, or the model will
        // be constrained to a shape the hand-rolled parser was not written to expect.
        private static Dictionary<string, object> SchemaString(bool nullable)
        {
            if (!nullable) return new Dictionary<string, object> { { "type", "string" } };
            return new Dictionary<string, object>
            {
                { "anyOf", new List<object>
                    {
                        new Dictionary<string, object> { { "type", "string" } },
                        new Dictionary<string, object> { { "type", "null" } }
                    }
                }
            };
        }

        private static Dictionary<string, object> SchemaNumber() =>
            new Dictionary<string, object> { { "type", "number" } };

        private static Dictionary<string, object> SchemaNumberNullable() =>
            new Dictionary<string, object>
            {
                { "anyOf", new List<object>
                    {
                        new Dictionary<string, object> { { "type", "number" } },
                        new Dictionary<string, object> { { "type", "null" } }
                    }
                }
            };

        private static Dictionary<string, object> SchemaArray(object items) =>
            new Dictionary<string, object> { { "type", "array" }, { "items", items } };

        private static Dictionary<string, object> SchemaObject(Dictionary<string, object> properties) =>
            new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties },
                { "required", new List<object>(properties.Keys) },
                { "additionalProperties", false }
            };

        private static Dictionary<string, object> BuildResponseSchema()
        {
            var onBadProps = new Dictionary<string, object>
            {
                { "type", SchemaString(false) },
                { "subtype", SchemaString(true) },
                { "faction", SchemaString(true) },
                { "intensity", SchemaNumber() },
                { "flavor", SchemaString(false) }
            };
            var onBadSchema = new Dictionary<string, object>
            {
                { "anyOf", new List<object> { SchemaObject(onBadProps), new Dictionary<string, object> { { "type", "null" } } } }
            };

            var eventProps = new Dictionary<string, object>
            {
                { "delay_hours", SchemaNumber() },
                { "type", SchemaString(false) },
                { "subtype", SchemaString(true) },
                { "arrival_mode", SchemaString(true) },
                { "pawn_kind", SchemaString(true) },
                { "pawn_count", SchemaNumberNullable() },
                { "faction", SchemaString(true) },
                { "intensity", SchemaNumber() },
                { "circle_step", SchemaString(false) },
                { "link", SchemaString(false) },
                { "link_reason", SchemaString(true) },
                { "expect", SchemaString(true) },
                { "fire_when", SchemaString(false) },
                { "note", SchemaString(true) },
                { "flavor", SchemaString(false) },
                { "on_bad", onBadSchema }
            };
            var eventSchema = SchemaObject(eventProps);

            var arcProps = new Dictionary<string, object>
            {
                { "decision", SchemaString(false) },
                { "arc_name", SchemaString(true) },
                { "arc_faction", SchemaString(true) },
                { "story_question", SchemaString(true) },
                { "arc_reserved_types", SchemaArray(SchemaString(false)) },
                { "queued_beats_action", SchemaString(true) },
                { "arc_summary_so_far", SchemaString(true) },
                { "unresolved_threads", SchemaArray(SchemaString(false)) },
                { "events", SchemaArray(eventSchema) },
                { "arc_reasoning", SchemaString(true) },
                { "arc_flavor", SchemaString(true) },
                { "closing_flavor", SchemaString(true) }
            };
            var arcSchema = SchemaObject(arcProps);

            var scatteredProps = new Dictionary<string, object>
            {
                { "delay_hours", SchemaNumber() },
                { "type", SchemaString(false) },
                { "subtype", SchemaString(true) },
                { "arrival_mode", SchemaString(true) },
                { "pawn_kind", SchemaString(true) },
                { "pawn_count", SchemaNumberNullable() },
                { "faction", SchemaString(true) },
                { "intensity", SchemaNumber() },
                { "note", SchemaString(true) },
                { "flavor", SchemaString(false) }
            };
            var scatteredSchema = SchemaObject(scatteredProps);

            var postureProps = new Dictionary<string, object>
            {
                { "current_blend", SchemaString(false) },
                { "posture_reasoning", SchemaString(true) },
                { "next_posture_hint", SchemaString(true) }
            };
            var postureSchema = SchemaObject(postureProps);

            var rootProps = new Dictionary<string, object>
            {
                { "arc", arcSchema },
                { "scattered_events", SchemaArray(scatteredSchema) },
                { "posture", postureSchema },
                { "next_call_days", SchemaNumber() },
                { "overall_reasoning", SchemaString(false) }
            };
            return SchemaObject(rootProps);
        }

        // ========== Legacy calls (kept for fallback) ==========

        public async Task<ClaudeResponse> GetMinorDecision(ColonyState state)
        {
            return await MakeSingleEventCall(state, UNIFIED_SYSTEM_PROMPT);
        }

        public async Task<ClaudeResponse> GetMajorDecision(ColonyState state)
        {
            return await MakeSingleEventCall(state, UNIFIED_SYSTEM_PROMPT);
        }

        public Task<NarrativeArcResponse> GetNarrativeArc(ColonyState state)
        {
            // Legacy — unified call handles this now
            return Task.FromResult<NarrativeArcResponse>(null);
        }

        // ========== Parsing ==========

        private UnifiedResponse ParseUnifiedResponse(string json)
        {
            try
            {
                var response = new UnifiedResponse();
                response.OverallReasoning = ExtractStringValue(json, "overall_reasoning");
                if (string.IsNullOrEmpty(response.OverallReasoning))
                {
                    // Fallback for an old or malformed response that omits overall_reasoning.
                    // A plain ExtractStringValue(json, "reasoning") over the WHOLE document risks
                    // matching a "reasoning"-keyed field nested under "arc" or "posture" instead —
                    // those come earlier in the document. Restrict the search to the tail of the
                    // document: after the posture block if present, else after the arc block. If
                    // neither is present (or the fallback still finds nothing there), leave
                    // OverallReasoning null rather than risk grabbing a nested field — no logging,
                    // this is a best-effort fallback for a field the model is not required to send.
                    int searchFrom = -1;

                    int postureKeyIdx = json.IndexOf("\"posture\"");
                    if (postureKeyIdx >= 0)
                    {
                        string postureBlockForScope = ExtractGuardedBlock(json, postureKeyIdx);
                        if (postureBlockForScope != null)
                            searchFrom = json.IndexOf('{', postureKeyIdx) + postureBlockForScope.Length;
                    }
                    else
                    {
                        int arcKeyIdx = json.IndexOf("\"arc\"");
                        if (arcKeyIdx >= 0)
                        {
                            string arcBlockForScope = ExtractGuardedBlock(json, arcKeyIdx);
                            if (arcBlockForScope != null)
                                searchFrom = json.IndexOf('{', arcKeyIdx) + arcBlockForScope.Length;
                        }
                    }

                    if (searchFrom >= 0 && searchFrom < json.Length)
                    {
                        string tail = json.Substring(searchFrom);
                        string fallback = ExtractStringValue(tail, "reasoning");
                        if (!string.IsNullOrEmpty(fallback))
                            response.OverallReasoning = fallback;
                    }
                }
                response.NextCallDays = ExtractFloatValue(json, "next_call_days", 3.0f);

                // Parse posture — guarded: the first non-whitespace char after the colon must
                // be '{', otherwise "posture": null would grab a later object's brace.
                int postureStart = json.IndexOf("\"posture\"");
                if (postureStart >= 0)
                {
                    string postureJson = ExtractGuardedBlock(json, postureStart);
                    if (postureJson != null)
                    {
                        response.Posture = new StorytellingPosture
                        {
                            CurrentBlend = ExtractStringValue(postureJson, "current_blend"),
                            Reasoning = ExtractStringValue(postureJson, "posture_reasoning"),
                            NextPostureHint = ExtractStringValue(postureJson, "next_posture_hint")
                        };
                    }
                }

                // Parse arc — same guard: "arc": null must not fall through to the next brace.
                int arcStart = json.IndexOf("\"arc\"");
                if (arcStart >= 0)
                {
                    string arcJson = ExtractGuardedBlock(json, arcStart);
                    if (arcJson != null)
                    {
                        response.Arc = new NarrativeArcDecision
                        {
                            Decision = ExtractStringValue(arcJson, "decision"),
                            ArcName = ExtractStringValue(arcJson, "arc_name"),
                            ArcFaction = ExtractStringValue(arcJson, "arc_faction"),
                            ArcFlavor = ExtractStringValue(arcJson, "arc_flavor"),
                            Reasoning = ExtractStringValue(arcJson, "arc_reasoning"),
                            StoryQuestion = ExtractStringValue(arcJson, "story_question"),
                            ClosingFlavor = ExtractStringValue(arcJson, "closing_flavor"),
                            Events = ParseArcEvents(arcJson),
                            ArcReservedTypes = ExtractStringArray(arcJson, "arc_reserved_types"),
                            QueuedBeatsAction = ExtractStringValue(arcJson, "queued_beats_action"),
                            ArcSummarySoFar = ExtractStringValue(arcJson, "arc_summary_so_far"),
                            UnresolvedThreads = ExtractStringArray(arcJson, "unresolved_threads")
                        };
                    }
                }

                // Parse scattered_events array
                response.ScatteredEvents = ParseScatteredEvents(json);

                int scatteredCount = response.ScatteredEvents?.Count ?? 0;
                int arcEventCount = response.Arc?.Events?.Count ?? 0;

                ClaudeLogger.LogEntry("UNIFIED_PARSED",
                    $"Arc: {response.Arc?.Decision ?? "null"} ({arcEventCount} events), " +
                    $"Scattered: {scatteredCount} events, " +
                    $"NextCall: {response.NextCallDays}d, " +
                    $"Posture: {response.Posture?.CurrentBlend ?? "null"}"
                );

                return response;
            }
            catch (Exception ex)
            {
                ClaudeLogger.LogApiError("ParseUnifiedResponse failed", ex.Message);
                return null;
            }
        }

        private List<ScatteredEvent> ParseScatteredEvents(string json)
        {
            var events = new List<ScatteredEvent>();

            int sectionStart = json.IndexOf("\"scattered_events\"");
            if (sectionStart < 0) return events;

            int arrayStart = json.IndexOf('[', sectionStart);
            if (arrayStart < 0) return events;

            // Find matching close bracket
            int depth = 1;
            int arrayEnd = arrayStart + 1;
            while (arrayEnd < json.Length && depth > 0)
            {
                if (json[arrayEnd] == '[') depth++;
                else if (json[arrayEnd] == ']') depth--;
                arrayEnd++;
            }

            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 2);

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                int objStart = arrayContent.IndexOf('{', pos);
                if (objStart < 0) break;

                string eventJson = ExtractBracedBlock(arrayContent, objStart);
                if (eventJson == null) break;

                var scattered = new ScatteredEvent
                {
                    DelayHours = ExtractFloatValue(eventJson, "delay_hours", 0),
                    Type = ExtractStringValue(eventJson, "type"),
                    Subtype = ExtractStringValue(eventJson, "subtype"),
                    ArrivalMode = ExtractStringValue(eventJson, "arrival_mode"),
                    PawnKind = ExtractStringValue(eventJson, "pawn_kind"),
                    PawnCount = (int)ExtractFloatValue(eventJson, "pawn_count", 0),
                    Faction = ExtractStringValue(eventJson, "faction"),
                    Intensity = ExtractFloatValue(eventJson, "intensity", 1.0f),
                    Animal = ExtractStringValue(eventJson, "animal"),
                    Note = ExtractStringValue(eventJson, "note"),
                            Flavor = ExtractStringValue(eventJson, "flavor")
                };

                events.Add(scattered);
                pos = objStart + eventJson.Length;
            }

            return events;
        }

        private EventDecision ParseEventDecision(string json, string sectionName)
        {
            int sectionStart = json.IndexOf($"\"{sectionName}\"");
            if (sectionStart < 0) return null;

            int braceStart = json.IndexOf('{', sectionStart);
            if (braceStart < 0) return null;

            string sectionJson = ExtractBracedBlock(json, braceStart);
            if (sectionJson == null) return null;

            var decision = new EventDecision
            {
                Decision = ExtractStringValue(sectionJson, "decision"),
                Reasoning = ExtractStringValue(sectionJson, "reasoning"),
                NarrativeIntent = ExtractStringValue(sectionJson, "narrative_intent")
            };

            // Parse event sub-object if present
            int eventStart = sectionJson.IndexOf("\"event\"");
            if (eventStart >= 0)
            {
                int eventBrace = sectionJson.IndexOf('{', eventStart);
                if (eventBrace >= 0)
                {
                    string eventJson = ExtractBracedBlock(sectionJson, eventBrace);
                    if (eventJson != null)
                    {
                        decision.Event = new EventChoice
                        {
                            Type = ExtractStringValue(eventJson, "type"),
                            Subtype = ExtractStringValue(eventJson, "subtype"),
                            Faction = ExtractStringValue(eventJson, "faction"),
                            Intensity = ExtractFloatValue(eventJson, "intensity", 1.0f),
                            DelayHours = ExtractIntValue(eventJson, "delay_hours", 0),
                            Animal = ExtractStringValue(eventJson, "animal"),
                            Note = ExtractStringValue(eventJson, "note"),
                            Flavor = ExtractStringValue(eventJson, "flavor")
                        };
                    }
                }
            }

            return decision;
        }

        private List<ArcEvent> ParseArcEvents(string arcJson)
        {
            var events = new List<ArcEvent>();

            int eventsStart = arcJson.IndexOf("\"events\"");
            if (eventsStart < 0) return events;

            int arrayStart = arcJson.IndexOf('[', eventsStart);
            if (arrayStart < 0) return events;

            int depth = 1;
            int arrayEnd = arrayStart + 1;
            while (arrayEnd < arcJson.Length && depth > 0)
            {
                if (arcJson[arrayEnd] == '[') depth++;
                else if (arcJson[arrayEnd] == ']') depth--;
                arrayEnd++;
            }

            string arrayContent = arcJson.Substring(arrayStart + 1, arrayEnd - arrayStart - 2);

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                int objStart = arrayContent.IndexOf('{', pos);
                if (objStart < 0) break;

                string eventJson = ExtractBracedBlock(arrayContent, objStart);
                if (eventJson == null) break;

                // Advance by the ORIGINAL block length, not the length after on_bad stripping
                // below — eventJson is reassigned to a shorter string, and using its length here
                // would desync `pos` from arrayContent and corrupt the rest of the array scan.
                int originalLength = eventJson.Length;

                // Guarded the same way as arc/posture: "on_bad": null must not fall through
                // to a later unrelated object.
                string onBadRaw = null;
                int onBadIdx = eventJson.IndexOf("\"on_bad\"");
                if (onBadIdx >= 0)
                {
                    onBadRaw = ExtractGuardedBlock(eventJson, onBadIdx);
                    if (onBadRaw != null)
                    {
                        // on_bad has its OWN "flavor"/"faction"/"subtype"/"intensity"/"type" keys.
                        // The extractors below match the first occurrence of each key anywhere in
                        // eventJson, so left in place, on_bad's sub-object could shadow the beat's
                        // own fields (e.g. if on_bad's "flavor" happens to appear before the
                        // beat's real "flavor" key). Strip the whole "on_bad": {...} substring out
                        // of eventJson before reading the beat's own fields.
                        int blockStart = eventJson.IndexOf('{', onBadIdx);
                        int blockEnd = blockStart + onBadRaw.Length;
                        eventJson = eventJson.Remove(onBadIdx, blockEnd - onBadIdx);
                    }
                }

                var arcEvent = new ArcEvent
                {
                    DelayHours = ExtractFloatValue(eventJson, "delay_hours", 0),
                    Type = ExtractStringValue(eventJson, "type"),
                    Subtype = ExtractStringValue(eventJson, "subtype"),
                    ArrivalMode = ExtractStringValue(eventJson, "arrival_mode"),
                    PawnKind = ExtractStringValue(eventJson, "pawn_kind"),
                    PawnCount = (int)ExtractFloatValue(eventJson, "pawn_count", 0),
                    Faction = ExtractStringValue(eventJson, "faction"),
                    Intensity = ExtractFloatValue(eventJson, "intensity", 1.0f),
                    Animal = ExtractStringValue(eventJson, "animal"),
                    Note = ExtractStringValue(eventJson, "note"),
                    Flavor = ExtractStringValue(eventJson, "flavor"),
                    CircleStep = ExtractStringValue(eventJson, "circle_step"),
                    Link = ExtractStringValue(eventJson, "link"),
                    LinkReason = ExtractStringValue(eventJson, "link_reason"),
                    Expect = ExtractStringValue(eventJson, "expect"),
                    FireWhen = ExtractStringValue(eventJson, "fire_when"),
                    OnBadJson = onBadRaw
                };

                events.Add(arcEvent);
                pos = objStart + originalLength;
            }

            return events;
        }

        private TimerAdjustment ParseTimerAdjustment(string json)
        {
            int adjStart = json.IndexOf("\"adjust_timers\"");
            if (adjStart < 0) return null;

            int colonPos = json.IndexOf(':', adjStart);
            if (colonPos < 0) return null;
            string afterColon = json.Substring(colonPos + 1).TrimStart();
            if (afterColon.StartsWith("null")) return null;

            int braceStart = json.IndexOf('{', adjStart);
            if (braceStart < 0) return null;

            string adjJson = ExtractBracedBlock(json, braceStart);
            if (adjJson == null) return null;

            return new TimerAdjustment
            {
                MinorMinHours = ExtractFloatValue(adjJson, "minor_min_hours", 0),
                MinorMaxHours = ExtractFloatValue(adjJson, "minor_max_hours", 0),
                MajorMinDays = ExtractFloatValue(adjJson, "major_min_days", 0),
                MajorMaxDays = ExtractFloatValue(adjJson, "major_max_days", 0),
                NarrativeMinDays = ExtractFloatValue(adjJson, "narrative_min_days", 0),
                NarrativeMaxDays = ExtractFloatValue(adjJson, "narrative_max_days", 0)
            };
        }

        // ========== Legacy single-event call (kept for compatibility) ==========

        private async Task<ClaudeResponse> MakeSingleEventCall(ColonyState state, string systemPrompt)
        {
            if (!CanMakeCall()) return null;

            try
            {
                RecordCallTime();

                var requestClient = new HttpClient();
                requestClient.DefaultRequestHeaders.Clear();
                requestClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                requestClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                string stateJson = SimpleJson.Serialize(state);
                ClaudeLogger.LogStateRequest(stateJson);

                string requestBody = SimpleJson.Serialize(new
                {
                    model = MODEL,
                    max_tokens = 4000,
                    system = systemPrompt,
                    messages = new[]
                    {
                        new { role = "user", content = stateJson }
                    }
                });

                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await requestClient.PostAsync(API_URL, content);
                string responseJson = await response.Content.ReadAsStringAsync();
                ClaudeLogger.LogRawResponse(responseJson);

                if (!response.IsSuccessStatusCode)
                {
                    ClaudeLogger.LogApiError($"HTTP {response.StatusCode}", responseJson);
                    return null;
                }

                LogUsage(responseJson, "single");

                string claudeText = ExtractTextContent(responseJson);
                if (string.IsNullOrEmpty(claudeText))
                {
                    ClaudeLogger.LogApiError("Failed to extract text from response", responseJson);
                    return null;
                }

                ClaudeLogger.LogEntry("EXTRACTED_JSON", claudeText);
                var parsed = ParseClaudeResponse(claudeText);

                ClaudeLogger.LogParsedDecision(
                    parsed?.Decision ?? "null",
                    parsed?.Event?.Type,
                    parsed?.Reasoning ?? "none",
                    parsed?.NarrativeIntent ?? "none"
                );

                return parsed;
            }
            catch (Exception ex)
            {
                ClaudeLogger.LogApiError(ex.Message, ex.StackTrace);
                return null;
            }
        }

        private ClaudeResponse ParseClaudeResponse(string json)
        {
            try
            {
                var response = new ClaudeResponse();
                response.Decision = ExtractStringValue(json, "decision");
                response.Reasoning = ExtractStringValue(json, "reasoning");
                response.NarrativeIntent = ExtractStringValue(json, "narrative_intent");
                response.AdjustTimers = ParseTimerAdjustment(json);

                int eventStart = json.IndexOf("\"event\"");
                if (eventStart >= 0)
                {
                    int braceStart = json.IndexOf('{', eventStart);
                    if (braceStart >= 0)
                    {
                        string eventJson = ExtractBracedBlock(json, braceStart);
                        if (eventJson != null)
                        {
                            response.Event = new EventChoice
                            {
                                Category = ExtractStringValue(eventJson, "category"),
                                Type = ExtractStringValue(eventJson, "type"),
                                Subtype = ExtractStringValue(eventJson, "subtype"),
                                Faction = ExtractStringValue(eventJson, "faction"),
                                Intensity = ExtractFloatValue(eventJson, "intensity", 1.0f),
                                DelayHours = ExtractIntValue(eventJson, "delay_hours", 0),
                                Animal = ExtractStringValue(eventJson, "animal"),
                                Note = ExtractStringValue(eventJson, "note"),
                            Flavor = ExtractStringValue(eventJson, "flavor")
                            };
                        }
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                ClaudeLogger.LogApiError("ParseClaudeResponse failed", ex.Message);
                return null;
            }
        }

        // ========== String/JSON utilities ==========

        /// <summary>
        /// Given the index of a "key" token, finds the colon after it and returns the braced
        /// object that follows IF the first non-whitespace character after the colon is '{'.
        /// Guards against "key": null (or any non-object value) being followed later in the
        /// document by an unrelated object, which a naive IndexOf('{', keyStart) would grab.
        /// </summary>
        private static string ExtractGuardedBlock(string json, int keyStart)
        {
            int colonPos = json.IndexOf(':', keyStart);
            if (colonPos < 0) return null;

            int cursor = colonPos + 1;
            while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;

            if (cursor >= json.Length || json[cursor] != '{') return null;
            return ExtractBracedBlock(json, cursor);
        }

        private static string ExtractBracedBlock(string json, int braceStart)
        {
            int depth = 1;
            int braceEnd = braceStart + 1;
            bool inString = false;
            bool escaped = false;

            while (braceEnd < json.Length && depth > 0)
            {
                char c = json[braceEnd];
                if (escaped) { escaped = false; }
                else if (c == '\\') { escaped = true; }
                else if (c == '"') { inString = !inString; }
                else if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                braceEnd++;
            }

            if (depth != 0) return null;
            return json.Substring(braceStart, braceEnd - braceStart);
        }

        private static string ExtractStringValue(string json, string key)
        {
            string pattern = $"\"{key}\":";
            int keyIndex = json.IndexOf(pattern);
            if (keyIndex < 0) return null;

            int valueStart = keyIndex + pattern.Length;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            if (valueStart >= json.Length) return null;
            if (json.Substring(valueStart).StartsWith("null")) return null;
            if (json[valueStart] != '"') return null;

            valueStart++;
            StringBuilder sb = new StringBuilder();
            bool esc = false;
            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { sb.Append(c); esc = false; }
                else if (c == '\\') { esc = true; }
                else if (c == '"') { break; }
                else { sb.Append(c); }
            }

            return sb.ToString();
        }

        private static float ExtractFloatValue(string json, string key, float defaultValue)
        {
            string pattern = $"\"{key}\":";
            int keyIndex = json.IndexOf(pattern);
            if (keyIndex < 0) return defaultValue;

            int valueStart = keyIndex + pattern.Length;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            StringBuilder sb = new StringBuilder();
            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];
                if (char.IsDigit(c) || c == '.' || c == '-')
                    sb.Append(c);
                else
                    break;
            }

            if (float.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
        }

        private static int ExtractIntValue(string json, string key, int defaultValue)
        {
            return (int)ExtractFloatValue(json, key, defaultValue);
        }

        /// <summary>
        /// Extracts a JSON array of quoted strings (e.g. "arc_reserved_types": ["A", "B"]).
        /// There is no general array-of-objects extractor reused here on purpose — this only
        /// needs to split top-level quoted strings inside the bracket, not nested objects.
        /// Guarded the same way as ExtractGuardedBlock: only whitespace between the colon and
        /// the '[' counts as this key's array.
        /// </summary>
        private static List<string> ExtractStringArray(string json, string key)
        {
            var result = new List<string>();
            string pattern = $"\"{key}\":";
            int keyIndex = json.IndexOf(pattern);
            if (keyIndex < 0) return result;

            int cursor = keyIndex + pattern.Length;
            while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
            if (cursor >= json.Length || json[cursor] != '[') return result;

            int arrayStart = cursor;
            int depth = 1;
            int i = arrayStart + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') depth--;
                i++;
            }
            if (depth != 0) return result;

            string content = json.Substring(arrayStart + 1, i - arrayStart - 2);

            bool inString = false;
            bool escaped = false;
            var sb = new StringBuilder();
            foreach (char c in content)
            {
                if (escaped) { sb.Append(c); escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"')
                {
                    inString = !inString;
                    if (!inString) { result.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                if (inString) sb.Append(c);
            }

            return result;
        }

        /// <summary>
        /// Public wrappers so ClaudeStorytellerComp can decode an on_bad block (raw braced JSON
        /// stored on QueuedEvent.OnBadJson) at fire time without duplicating the string parser.
        /// </summary>
        public static string ExtractOnBadString(string onBadJson, string key)
        {
            if (string.IsNullOrEmpty(onBadJson)) return null;
            return ExtractStringValue(onBadJson, key);
        }

        public static float ExtractOnBadFloat(string onBadJson, string key, float defaultValue)
        {
            if (string.IsNullOrEmpty(onBadJson)) return defaultValue;
            return ExtractFloatValue(onBadJson, key, defaultValue);
        }

        private void LogUsage(string responseJson, string callType)
        {
            try
            {
                int inTok = ExtractIntValue(responseJson, "input_tokens", 0);
                int outTok = ExtractIntValue(responseJson, "output_tokens", 0);
                int cacheRead = ExtractIntValue(responseJson, "cache_read_input_tokens", 0);
                if (inTok == 0 && outTok == 0) return;

                sessionCalls++;
                sessionInputTokens += inTok;
                sessionOutputTokens += outTok;

                double callCost = (inTok * INPUT_COST_PER_MTOK + outTok * OUTPUT_COST_PER_MTOK) / 1000000.0;
                double sessionCost = (sessionInputTokens * INPUT_COST_PER_MTOK
                                    + sessionOutputTokens * OUTPUT_COST_PER_MTOK) / 1000000.0;

                ClaudeLogger.LogEntry("USAGE",
                    $"[{callType}] {inTok} in / {outTok} out" +
                    (cacheRead > 0 ? $" ({cacheRead} cached)" : "") +
                    $" = ${callCost:F4}\n" +
                    $"Session total: {sessionCalls} call(s), " +
                    $"{sessionInputTokens} in / {sessionOutputTokens} out = ${sessionCost:F4}"
                );
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] LogUsage failed: {ex.Message}");
            }
        }

        private string ExtractTextContent(string responseJson)
        {
            try
            {
                string marker = "\"text\":\"";
                int textStart = responseJson.IndexOf(marker);
                if (textStart < 0)
                {
                    marker = "\"text\": \"";
                    textStart = responseJson.IndexOf(marker);
                }

                if (textStart < 0) return null;
                textStart += marker.Length;

                StringBuilder sb = new StringBuilder();
                bool escaped = false;

                for (int i = textStart; i < responseJson.Length; i++)
                {
                    char c = responseJson[i];
                    if (escaped)
                    {
                        if (c == 'n') sb.Append('\n');
                        else if (c == 't') sb.Append('\t');
                        else if (c == 'r') sb.Append('\r');
                        else if (c == '"') sb.Append('"');
                        else if (c == '\\') sb.Append('\\');
                        else sb.Append(c);
                        escaped = false;
                    }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { break; }
                    else { sb.Append(c); }
                }

                string extracted = sb.ToString().Trim();
                int jsonStart = extracted.IndexOf('{');
                int jsonEnd = extracted.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    return extracted.Substring(jsonStart, jsonEnd - jsonStart + 1);

                return extracted;
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClaudeStoryteller] ExtractTextContent failed: {ex.Message}");
                return null;
            }
        }

        public static bool ValidateApiKey(string key)
        {
            return !string.IsNullOrEmpty(key) && key.StartsWith("sk-ant-");
        }
    }
}
