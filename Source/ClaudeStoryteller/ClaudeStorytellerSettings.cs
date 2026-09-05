using System;
using System.Text;
using Verse;
using UnityEngine;

namespace ClaudeStoryteller
{
    public class ClaudeStorytellerSettings : ModSettings
    {
        private string encryptedApiKey = "";
        private string cachedDecryptedKey = null;
        public bool enabled = true;

        // Surface Claude's arc names, reasoning and per-event notes as in-game letters.
        public bool showNarrativeLetters = true;

        // Bounds on how often the unified call runs. Claude picks next_call_days per
        // response; these clamp its choice, so a narrow range overrides its pacing.
        public float callMinDays = 2f;
        public float callMaxDays = 7f;

        public static readonly float CALL_FLOOR_DAYS = 1f;
        public static readonly float CALL_CEILING_DAYS = 15f;

        // Force-end an arc that runs too long or stalls with nothing queued.
        public float maxArcDays = 24f;
        public float arcStallDays = 10f;

        public static readonly float MAX_ARC_DAYS_FLOOR = 8f;
        public static readonly float MAX_ARC_DAYS_CEILING = 40f;
        public static readonly float ARC_STALL_DAYS_FLOOR = 4f;
        public static readonly float ARC_STALL_DAYS_CEILING = 20f;

        // Phase 2: how many scattered ("meanwhile") events are kept per call while an arc is
        // active; extras are trimmed (SCATTERED_TRIMMED).
        public float maxScatteredDuringArc = 3f;
        public static readonly float MAX_SCATTERED_DURING_ARC_FLOOR = 0f;
        public static readonly float MAX_SCATTERED_DURING_ARC_CEILING = 6f;

        // Gated behind this flag, no behavior change yet (Phase 3 wires the request body).
        public bool useStructuredOutputs = false;

        private static readonly byte[] ObfuscationKey = { 0x43, 0x6C, 0x61, 0x75, 0x64, 0x65, 0x41, 0x49 };

        public string ApiKey
        {
            get
            {
                if (cachedDecryptedKey == null && !string.IsNullOrEmpty(encryptedApiKey))
                {
                    cachedDecryptedKey = Deobfuscate(encryptedApiKey);
                }
                return cachedDecryptedKey ?? "";
            }
            set
            {
                cachedDecryptedKey = value;
                encryptedApiKey = string.IsNullOrEmpty(value) ? "" : Obfuscate(value);
            }
        }

        public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

        public string GetMaskedApiKey()
        {
            string key = ApiKey;
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 10) return "••••••••";
            return key.Substring(0, 7) + "•••••••••••••" + key.Substring(key.Length - 4);
        }

        private string Obfuscate(string plaintext)
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= ObfuscationKey[i % ObfuscationKey.Length];
            }
            return Convert.ToBase64String(data);
        }

        private string Deobfuscate(string encoded)
        {
            try
            {
                byte[] data = Convert.FromBase64String(encoded);
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] ^= ObfuscationKey[i % ObfuscationKey.Length];
                }
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref encryptedApiKey, "apiKey", "");
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref showNarrativeLetters, "showNarrativeLetters", true);
            Scribe_Values.Look(ref callMinDays, "callMinDays", 2f);
            Scribe_Values.Look(ref callMaxDays, "callMaxDays", 7f);
            Scribe_Values.Look(ref maxArcDays, "maxArcDays", 24f);
            Scribe_Values.Look(ref arcStallDays, "arcStallDays", 10f);
            Scribe_Values.Look(ref maxScatteredDuringArc, "maxScatteredDuringArc", 3f);
            Scribe_Values.Look(ref useStructuredOutputs, "useStructuredOutputs", false);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                cachedDecryptedKey = null;
            }
            
            base.ExposeData();
        }

        public float CallMinDays => Mathf.Clamp(callMinDays, CALL_FLOOR_DAYS, CALL_CEILING_DAYS);

        public float CallMaxDays => Mathf.Max(CallMinDays, Mathf.Clamp(callMaxDays, CALL_FLOOR_DAYS, CALL_CEILING_DAYS));

        public float ClampCallDays(float days) => Mathf.Clamp(days, CallMinDays, CallMaxDays);

    }

    public class ClaudeStorytellerMod : Mod
    {
        public static ClaudeStorytellerSettings settings;
        private string testResult = "";
        private bool testInProgress = false;
        private string inputBuffer = "";
        private bool showKeyEntry = false;

        public ClaudeStorytellerMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<ClaudeStorytellerSettings>();
            Log.Message("[ClaudeStoryteller] Mod loaded successfully!");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("Claude API Key:");
            
            if (settings.HasApiKey && !showKeyEntry)
            {
                // Key exists and we're not editing - show masked version
                listing.Label($"  {settings.GetMaskedApiKey()}");
                listing.Label("API key format: Valid");
                
                if (listing.ButtonText("Change API Key"))
                {
                    showKeyEntry = true;
                    inputBuffer = "";
                }
                
                if (testInProgress)
                {
                    listing.Label("Testing connection...");
                }
                else
                {
                    if (listing.ButtonText("Test API Connection"))
                    {
                        TestApiConnection();
                    }

                    if (!string.IsNullOrEmpty(testResult))
                    {
                        listing.Label($"Result: {testResult}");
                    }
                }
            }
            else
            {
                // No key or actively editing - show entry field
                listing.Label("Enter your API key from console.anthropic.com:");
                inputBuffer = listing.TextEntry(inputBuffer);
                
                bool validFormat = ClaudeApiClient.ValidateApiKey(inputBuffer);
                if (!string.IsNullOrEmpty(inputBuffer))
                {
                    listing.Label(validFormat ? "Format: Valid" : "Format: Invalid (should start with sk-ant-)");
                }
                
                if (validFormat)
                {
                    if (listing.ButtonText("Save API Key"))
                    {
                        settings.ApiKey = inputBuffer;
                        inputBuffer = "";
                        showKeyEntry = false;
                        testResult = "";
                    }
                }
                
                if (settings.HasApiKey)
                {
                    if (listing.ButtonText("Cancel"))
                    {
                        inputBuffer = "";
                        showKeyEntry = false;
                    }
                }
            }

            listing.Gap();
            listing.CheckboxLabeled("Enable Claude Storyteller", ref settings.enabled);
            listing.CheckboxLabeled("Show narrative letters", ref settings.showNarrativeLetters);
            listing.Label("  Sends Claude's arc names and event notes to your letter stack.");

            listing.Gap();
            listing.Label("Storyteller Call Interval");
            listing.Label($"  Every {settings.CallMinDays:F1} - {settings.CallMaxDays:F1} game days");
            settings.callMinDays = listing.Slider(settings.callMinDays,
                ClaudeStorytellerSettings.CALL_FLOOR_DAYS, ClaudeStorytellerSettings.CALL_CEILING_DAYS);
            settings.callMaxDays = listing.Slider(settings.callMaxDays,
                ClaudeStorytellerSettings.CALL_FLOOR_DAYS, ClaudeStorytellerSettings.CALL_CEILING_DAYS);
            if (settings.callMinDays > settings.callMaxDays)
                settings.callMaxDays = settings.callMinDays;

            listing.Label("  Claude decides when to next check on your colony; this clamps that");
            listing.Label("  choice. Narrower and lower = more responsive storyteller, higher API");
            listing.Label($"  cost (~$0.12 per call, so roughly ${0.12 * (30f / settings.CallMaxDays):F2} - ${0.12 * (30f / settings.CallMinDays):F2} per 30 game days).");
            listing.Label("  A narrative arc's pace is bounded by this same interval — it cannot advance");
            listing.Label("  faster than the storyteller calls back. 2-3 days is recommended while an arc is live.");

            listing.Gap();
            listing.Label("Arc Timeouts");
            listing.Label($"  Force-end an arc after {settings.maxArcDays:F0} days, or after {settings.arcStallDays:F0} days with no beat fired.");
            settings.maxArcDays = listing.Slider(settings.maxArcDays,
                ClaudeStorytellerSettings.MAX_ARC_DAYS_FLOOR, ClaudeStorytellerSettings.MAX_ARC_DAYS_CEILING);
            settings.arcStallDays = listing.Slider(settings.arcStallDays,
                ClaudeStorytellerSettings.ARC_STALL_DAYS_FLOOR, ClaudeStorytellerSettings.ARC_STALL_DAYS_CEILING);

            listing.Gap();
            listing.Label($"  Max scattered (\"meanwhile\") events per call while an arc is active: {settings.maxScatteredDuringArc:F0}");
            settings.maxScatteredDuringArc = listing.Slider(settings.maxScatteredDuringArc,
                ClaudeStorytellerSettings.MAX_SCATTERED_DURING_ARC_FLOOR, ClaudeStorytellerSettings.MAX_SCATTERED_DURING_ARC_CEILING);

            listing.Gap();
            listing.CheckboxLabeled("Use structured outputs (experimental)", ref settings.useStructuredOutputs);
            listing.Label("  Asks the API to guarantee the response's JSON shape instead of just asking nicely.");
            listing.Label("  Falls back automatically to a normal request if the API rejects the schema.");

            listing.Gap();
            listing.Gap();
            listing.Label($"Log: {ClaudeLogger.GetLogPath()}");

            listing.End();
            base.DoSettingsWindowContents(inRect);
        }

        private async void TestApiConnection()
        {
            testInProgress = true;
            testResult = "";

            try
            {
                testResult = await ClaudeApiClient.TestConnection(settings.ApiKey);
            }
            catch (Exception ex)
            {
                testResult = $"Error: {ex.Message}";
            }
            finally
            {
                testInProgress = false;
            }
        }

        public override string SettingsCategory()
        {
            return "Claude Storyteller";
        }
    }
}
