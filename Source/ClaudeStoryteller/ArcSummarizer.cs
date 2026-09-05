using System.Collections.Generic;
using System.Linq;

namespace ClaudeStoryteller
{
    public static class ArcSummarizer
    {
        /// <summary>
        /// Produces a compact summary of arc history for the Claude API payload.
        /// Identifies overused events, underused events, dominant patterns, and includes recent arcs.
        /// </summary>
        public static Models.ArcHistorySummary Summarize(List<ArcLogEntry> arcLog, List<string> allAvailableEvents)
        {
            var summary = new Models.ArcHistorySummary();
            summary.TotalArcs = arcLog.Count;

            if (arcLog.Count == 0)
            {
                summary.Last3Arcs = new List<Models.ArcSummaryEntry>();
                summary.OverusedEvents = new List<string>();
                summary.UnderusedEvents = allAvailableEvents.Take(8).ToList();
                summary.OverusedOpeners = new List<string>();
                summary.DominantPattern = "none — this is the first arc";
                summary.Instruction = "This is the first narrative arc. Be creative and set a strong opening tone.";
                return summary;
            }

            // Last 3 arcs with full event lists
            summary.Last3Arcs = arcLog
                .Skip(System.Math.Max(0, arcLog.Count - 3))
                .Select(a => new Models.ArcSummaryEntry
                {
                    Name = a.ArcName,
                    Events = a.Events,
                    Outcome = a.Outcome,
                    Day = a.StartDay,
                    Question = a.StoryQuestion,
                    Summary = a.Summary,
                    Deaths = a.Deaths,
                    ColonistDelta = a.ColonistDelta,
                    Links = a.Links,
                    Faction = a.Faction,
                    UnresolvedThreads = a.UnresolvedThreads
                })
                .ToList();

            // Count event usage across all arcs
            var eventCounts = new Dictionary<string, int>();
            var openerCounts = new Dictionary<string, int>();
            var pairCounts = new Dictionary<string, int>(); // "A → B" pair frequency

            foreach (var arc in arcLog)
            {
                if (arc.Events == null || arc.Events.Count == 0) continue;

                // Count opener
                string opener = arc.Events[0];
                openerCounts[opener] = openerCounts.ContainsKey(opener) ? openerCounts[opener] + 1 : 1;

                // Count all events
                foreach (var evt in arc.Events)
                {
                    eventCounts[evt] = eventCounts.ContainsKey(evt) ? eventCounts[evt] + 1 : 1;
                }

                // Count sequential pairs
                for (int i = 0; i < arc.Events.Count - 1; i++)
                {
                    string pair = $"{arc.Events[i]} → {arc.Events[i + 1]}";
                    pairCounts[pair] = pairCounts.ContainsKey(pair) ? pairCounts[pair] + 1 : 1;
                }
            }

            // Overused events (appeared in more than 50% of arcs, minimum 2 arcs)
            int threshold = System.Math.Max(2, arcLog.Count / 2);
            summary.OverusedEvents = eventCounts
                .Where(kv => kv.Value >= threshold)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} ({kv.Value}x in {arcLog.Count} arcs)")
                .ToList();

            // Overused openers (used more than once)
            summary.OverusedOpeners = openerCounts
                .Where(kv => kv.Value > 1)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} ({kv.Value}x as opener)")
                .ToList();

            // Underused events — available but never or rarely used. Capped at 8 so the prompt
            // does not grow unbounded as the event pool widens.
            var usedEvents = new HashSet<string>(eventCounts.Keys);
            summary.UnderusedEvents = allAvailableEvents
                .Where(e => !eventCounts.ContainsKey(e) || eventCounts[e] <= 1)
                .Take(8)
                .ToList();

            // Dominant pattern — most common sequential pair
            if (pairCounts.Count > 0)
            {
                var topPair = pairCounts.OrderByDescending(kv => kv.Value).First();
                if (topPair.Value >= 2)
                    summary.DominantPattern = $"{topPair.Key} (occurred {topPair.Value}x)";
                else
                    summary.DominantPattern = "no dominant pattern yet";
            }
            else
            {
                summary.DominantPattern = "no dominant pattern yet";
            }

            // Build instruction
            summary.Instruction = BuildInstruction(summary, arcLog, arcLog.Count);

            return summary;
        }

        private static string BuildInstruction(Models.ArcHistorySummary summary, List<ArcLogEntry> arcLog, int arcCount)
        {
            var lastArc = arcLog[arcLog.Count - 1];
            string prefix =
                $"The last arc asked: '{lastArc.StoryQuestion}' and ended '{lastArc.Outcome}' ({lastArc.Summary}). " +
                "Ask a different question and use a different first beat. ";

            if (lastArc.Outcome == "timed_out" || lastArc.Outcome == "stalled" || lastArc.Outcome == "abandoned")
            {
                prefix += "The previous arc was ended by the code because it stalled; end arcs deliberately with end_arc. ";
            }

            // Repeated connector-pair sequence identical to the previous arc.
            if (summary.Last3Arcs.Count >= 2)
            {
                var lastLinks = summary.Last3Arcs[summary.Last3Arcs.Count - 1].Links;
                var prevLinks = summary.Last3Arcs[summary.Last3Arcs.Count - 2].Links;
                if (lastLinks != null && prevLinks != null && lastLinks.Count > 1 && lastLinks.SequenceEqual(prevLinks))
                {
                    prefix += $"The last two arcs used the identical connector sequence ({string.Join("->", lastLinks)}). Vary it this time. ";
                }
            }

            // Repeated 'take' mechanism across the last 3 arcs.
            var recentTakeTypes = arcLog.Skip(System.Math.Max(0, arcLog.Count - 3))
                .SelectMany(a => a.TakeEventTypes ?? new List<string>())
                .ToList();
            var repeatedTake = recentTakeTypes.GroupBy(t => t).FirstOrDefault(g => g.Count() > 1);
            if (repeatedTake != null)
            {
                prefix += $"The 'take' mechanism {repeatedTake.Key} has repeated across recent arcs. Use a different price. ";
            }

            var parts = new List<string>();

            if (summary.OverusedOpeners.Count > 0)
            {
                parts.Add($"Do NOT open with: {string.Join(", ", summary.OverusedOpeners.Select(o => o.Split(' ')[0]))}");
            }

            if (summary.OverusedEvents.Count > 0)
            {
                parts.Add($"Reduce usage of: {string.Join(", ", summary.OverusedEvents.Select(o => o.Split(' ')[0]))}");
            }

            if (summary.UnderusedEvents.Count > 0)
            {
                var suggested = summary.UnderusedEvents.Take(5);
                parts.Add($"Consider using: {string.Join(", ", suggested)}");
            }

            if (summary.DominantPattern != "no dominant pattern yet" && summary.DominantPattern != "none — this is the first arc")
            {
                parts.Add($"Avoid the pattern: {summary.DominantPattern.Split('(')[0].Trim()}");
            }

            parts.Add("Create a structurally different arc from the previous ones. Surprise the player.");

            return prefix + string.Join(". ", parts) + ".";
        }
    }
}
