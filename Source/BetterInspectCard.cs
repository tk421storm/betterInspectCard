using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;
using BetterInfoCard;
using System.Security.Cryptography;

namespace TKS_BetterInspectCard
{
    [StaticConstructorOnStartup]
    public static class InsertHarmony
    {
        static InsertHarmony()
        {
            Harmony harmony = new Harmony("TKS_BetterInspectCard");
            //Harmony.DEBUG = true;
            harmony.PatchAll();
            //Harmony.DEBUG = false;
            Log.Message($"TKS_BetterInspectCard: Patching finished");
        }
    }


    public static class Utilities
    {

        private static Dictionary<string, string> statDrawEntryToLabelCache = new Dictionary<string, string>();
        //stats are cached per thing (the inspect string is built every frame) and rebuilt after this many seconds.
        //real time rather than ticks, so values also refresh while paused. could be exposed as a setting
        public static float statCacheRefreshSeconds = 1f;

        //clear the cache once it holds this many things, so it doesn't grow over a long session
        private const int statCacheMaxThings = 50;

        //thingIDNumber : (realtime the stats were built, stats)
        private static Dictionary<int, (float builtAt, List<StatDrawEntry> stats)> statDrawEntryCache = new Dictionary<int, (float builtAt, List<StatDrawEntry> stats)>();

        public static HashSet<string> GetFavoriteStatIDs()
        {
            BetterInfoCard.BetterInfoCardModSetting BetterInfoCardModSettings = LoadedModManager.GetMod<BetterInfoCard.BetterInfoCard>().GetSettings<BetterInfoCard.BetterInfoCardModSetting>();

            if (BetterInfoCardModSettings == null)
            { 
                TKS_BetterInspectCard.DebugMessage("Could not find BetterInfoCard settings");
                return new HashSet<string>();
            }

            return BetterInfoCardModSettings.FavoriteStatDrawIds;

        }

        public static string GetStatLabel(StatDrawEntry statDrawEntry)
        {
            string drawEntry = statDrawEntry?.Get_labelInt();

            if (drawEntry == null)
            {
                return null;
            }

            if (statDrawEntryToLabelCache.ContainsKey(drawEntry))
            {
                if (TKS_BetterInspectCard.DebugEnabled)
                {
                    TKS_BetterInspectCard.DebugMessage("Found cached label for " + drawEntry + ": " + statDrawEntryToLabelCache[drawEntry]);
                }
                return statDrawEntryToLabelCache[drawEntry];
            }

            if (statDrawEntry.stat == null)
            {
                StatDef statDef = DefDatabase<StatDef>.GetNamed(statDrawEntry.LabelCap, false);
                if (statDef != null)
                {
                    statDrawEntryToLabelCache.Add(drawEntry, statDef.label);
                    return statDef.label;
                }
            }

            statDrawEntryToLabelCache.Add(drawEntry, statDrawEntry.LabelCap);
            return statDrawEntry.LabelCap;
        }

        //this dictionary is only updated after gui draw, so in general is not helpful to us
        public static Dictionary<string, (string, StatCategoryDef, string)> GetRecordedLabelID()
        {
            BetterInfoCard.BetterInfoCard BetterInfoCardMod = LoadedModManager.GetMod<BetterInfoCard.BetterInfoCard>();

            if (BetterInfoCardMod == null)
            {
                TKS_BetterInspectCard.DebugMessage("Could not find BetterInfoCard mod");
                return null;
            }
            Dictionary<string, (string, StatCategoryDef, string)> recordedLabelID = BetterInfoCardMod.recordedLableId;
            if (recordedLabelID.Count==0)
            {
                Log.ErrorOnce("RecordedLabelID is empty, this is a problem", 123456);
            } else
            {
                foreach(var entry in recordedLabelID)
                {
                    TKS_BetterInspectCard.DebugMessage("RecordedLabelID entry: " + entry.Key + " -> " + entry.Value.Item1 + ", " + entry.Value.Item2.defName + ", " + entry.Value.Item3);
                }
            }

            //this dictionary is complicated but basically statDrawEntityID :  (StatDrawEntityID, statDef.category, override report text)
            return recordedLabelID;
        }

        public static bool IsFavorited(StatDrawEntry drawEntry)
        {
            if (drawEntry == null)
            {
                return false;
            }
            string statDrawEntryId = (drawEntry.stat?.defName) ?? drawEntry.Get_labelInt();

            //TKS_BetterInspectCard.DebugMessage("favorite stat ids (checking for "+statDrawEntryId+"): "+ string.Join(", ", GetFavoriteStatIDs()));
            //check if the category is in the favorite list
            HashSet<string> favoriteStatIDs = GetFavoriteStatIDs();

            if (favoriteStatIDs.Contains(statDrawEntryId))
            {
                //TKS_BetterInspectCard.DebugMessage("found " + statDef);
                //(string firstValue, StatCategoryDef secondValue, string thirdValue) = GetRecordedLabelID().FirstOrDefault(kvp => kvp.Value.Item2.defName == statDef.defName).Value;

                return true;
            }
            return false;
        }

        private static List<StatDrawEntry> GetStatsToDraw(ThingWithComps thing)
        {

            float now = Time.realtimeSinceStartup;

            if (statDrawEntryCache.TryGetValue(thing.thingIDNumber, out var cached) && now - cached.builtAt < statCacheRefreshSeconds)
            {
                if (TKS_BetterInspectCard.DebugEnabled)
                {
                    TKS_BetterInspectCard.DebugMessage("Found cached stats for " + thing);
                }
                return cached.stats;
            }

            List<StatDrawEntry> statsToDraw = new List<StatDrawEntry>();

            foreach (StatDrawEntry statDrawEntry in thing.SpecialDisplayStats().Concat(thing.def.SpecialDisplayStats(StatRequest.For(thing))))
            {
                statsToDraw.Add(statDrawEntry);
            }

            //only starred stats are drawn, so skip the rest before working out their values (pawns have a lot of stats)
            HashSet<string> favoriteStatIDs = GetFavoriteStatIDs();

            foreach (StatDef statDef in DefDatabase<StatDef>.AllDefs.Where<StatDef>((Func<StatDef, bool>)(st => favoriteStatIDs.Contains(st.defName) && st.Worker.ShouldShowFor(StatRequest.For(thing)))))
            {
                if (!statsToDraw.Any(sde => sde.stat == statDef))
                {
                    float statValue = statDef.Worker.GetValue(StatRequest.For(thing));

                    //hide the same stats the vanilla info card hides: values at a stat's hideAtValue (e.g. 100% immunity gain speed
                    //on every building) and default values for stats that don't show them
                    if (Mathf.Approximately(statValue, statDef.hideAtValue) || (!statDef.showOnDefaultValue && statValue == statDef.defaultBaseValue))
                    {
                        continue;
                    }

                    StatDrawEntry statDrawEntry = new StatDrawEntry(statDef.category, statDef.defName, statDef.ValueToString(statValue), "", 3555, null, null, false, false);
                    statsToDraw.Add(statDrawEntry);
                }

            }

            if (statDrawEntryCache.Count >= statCacheMaxThings)
            {
                statDrawEntryCache.Clear();
            }

            statDrawEntryCache[thing.thingIDNumber] = (now, statsToDraw);

            return statsToDraw;
        }

        public static string AdjustInspectString(string inspectString, ThingWithComps thing)
        {
            if (inspectString == null || thing == null)
            {
                return inspectString;
            }
            
            List<StatDrawEntry> statsToDraw = GetStatsToDraw(thing);

            if (statsToDraw == null || !statsToDraw.Any())
            {
                TKS_BetterInspectCard.DebugMessage("No stats to draw for " + thing);
                return inspectString;
            }

            if (TKS_BetterInspectCard.DebugEnabled)
            {
                TKS_BetterInspectCard.DebugMessage("statsToDraw for " + thing + ": " + string.Join(", ", statsToDraw.Select(x=> x.Get_labelInt())));
                TKS_BetterInspectCard.DebugMessage("favoriteStats for " + thing + ": " + string.Join(", ", GetFavoriteStatIDs()));
            }

            foreach (StatDrawEntry statDef in statsToDraw)
            {
                if (statDef == null || !Utilities.IsFavorited(statDef))
                { continue; }

                //adjust the string
                string statLabel = GetStatLabel(statDef);
                if (statLabel != null)
                {
                    if (TKS_BetterInspectCard.DebugEnabled)
                    {
                        TKS_BetterInspectCard.DebugMessage("statDef " + statLabel + " is starred and should be promoted");
                    }

                    if (inspectString.Length!=0 && !inspectString.EndsWith("\n"))
                    {
                        inspectString += "\n";
                    }
                    inspectString += "<color=yellow><b>" + statLabel + ":</b></color> " + statDef.ValueString;
                }

            }

            return inspectString;

        }
    }

    public class TKS_BetterInspectCardSettings : ModSettings
    {
        public bool debugPrint = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref debugPrint, "debugPrint");

            base.ExposeData();
        }
    }

    public class TKS_BetterInspectCard : Mod
    {
        static TKS_BetterInspectCardSettings settings;

        //check this before building a debug string on a hot path (the inspect string is built every frame), so the
        //string is only built when it will be printed
        public static bool DebugEnabled => settings != null && settings.debugPrint;

        public static void DebugMessage(string message)
        {
            if (DebugEnabled)
            {
                Log.Message(message);
            }
        }


        public TKS_BetterInspectCard(ModContentPack content) : base(content)
        {
            settings = GetSettings<TKS_BetterInspectCardSettings>();
        }

        private string editBufferFloat;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listingStandard = new Listing_Standard();
            listingStandard.Begin(inRect);
            listingStandard.CheckboxLabeled("TKSDebugPrint".Translate(), ref settings.debugPrint);
            listingStandard.End();
            base.DoSettingsWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "TKSBetterInspectCardName".Translate();
        }
    }

    //the inspect pane builds its text in DrawInspectStringFor and draws it with DrawInspectString. we add our lines at that point
    //rather than in GetInspectString, so they show for every thing class, including mod classes (e.g. Vanilla Plants Expanded
    //plants) that override GetInspectString without calling base, and can't be changed by other mods afterwards
    [HarmonyPatch(typeof(InspectPaneFiller))]
    internal static class InspectPaneFiller_Patches
    {
        //the thing whose inspect string is being drawn: set in DrawInspectStringFor, used and cleared in DrawInspectString
        private static ThingWithComps drawingThing = null;

        [HarmonyPatch(typeof(InspectPaneFiller), "DrawInspectStringFor")]
        [HarmonyPrefix]
        public static void DrawInspectStringFor_Prefix(ISelectable sel)
        {
            drawingThing = null;

            //pawns too: vanilla draws a pawn's inspect string the same way, below its health and mood rows
            if (sel is ThingWithComps thing)
            {
                drawingThing = thing;
            }
        }

        //clear again in case DrawInspectString was never reached (e.g. another mod skipped the original), so the next
        //DrawInspectString call (storage groups use it too) doesn't get our lines for the wrong thing
        [HarmonyPatch(typeof(InspectPaneFiller), "DrawInspectStringFor")]
        [HarmonyPostfix]
        public static void DrawInspectStringFor_Postfix()
        {
            drawingThing = null;
        }

        [HarmonyPatch(typeof(InspectPaneFiller), "DrawInspectString")]
        [HarmonyPrefix]
        public static void DrawInspectString_Prefix(ref string str)
        {
            if (drawingThing == null)
            {
                return;
            }

            ThingWithComps thing = drawingThing;
            drawingThing = null;

            if (TKS_BetterInspectCard.DebugEnabled)
            {
                TKS_BetterInspectCard.DebugMessage("adding favorited stats to inspect string for " + thing);
            }

            str = Utilities.AdjustInspectString(str, thing);
        }
    }
}

