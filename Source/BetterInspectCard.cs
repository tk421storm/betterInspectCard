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
        private static Dictionary<string, List<StatDrawEntry>> statDrawEntryCache = new Dictionary<string, List<StatDrawEntry>>();

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
                TKS_BetterInspectCard.DebugMessage("Found cached label for " + drawEntry + ": " + statDrawEntryToLabelCache[drawEntry]);
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

            if (statDrawEntryCache.ContainsKey(thing.def.defName))
            {
                TKS_BetterInspectCard.DebugMessage("Found cached stats for " + thing.def.defName);
                return statDrawEntryCache[thing.def.defName];
            }

            List<StatDrawEntry> statsToDraw = new List<StatDrawEntry>();

            foreach (StatDrawEntry statDrawEntry in thing.SpecialDisplayStats().Concat(thing.def.SpecialDisplayStats(StatRequest.For(thing))))
            {
                statsToDraw.Add(statDrawEntry);
            }

            foreach (StatDef statDef in DefDatabase<StatDef>.AllDefs.Where<StatDef>((Func<StatDef, bool>)(st => st.Worker.ShouldShowFor(StatRequest.For(thing)))))
            {
                if (!statsToDraw.Any(sde => sde.stat == statDef))
                {
                    StatDrawEntry statDrawEntry = new StatDrawEntry(statDef.category, statDef.defName, statDef.ValueToString(statDef.Worker.GetValue(StatRequest.For(thing))), "", 3555, null, null, false, false);
                    statsToDraw.Add(statDrawEntry);
                }

            }

            statDrawEntryCache.Add(thing.def.defName, statsToDraw);

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

            TKS_BetterInspectCard.DebugMessage("statsToDraw for " + thing + ": " + string.Join(", ", statsToDraw.Select(x=> x.Get_labelInt())));
            TKS_BetterInspectCard.DebugMessage("favoriteStats for " + thing + ": " + string.Join(", ", GetFavoriteStatIDs()));

            foreach (StatDrawEntry statDef in statsToDraw)
            {
                if (statDef == null || !Utilities.IsFavorited(statDef))
                { continue; }

                //adjust the string
                string statLabel = GetStatLabel(statDef);
                if (statLabel != null)
                {
                    TKS_BetterInspectCard.DebugMessage("statDef " + statLabel + " is starred and should be promoted");

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
        TKS_BetterInspectCardSettings settings;

        public static void DebugMessage(string message)
        {
            if (LoadedModManager.GetMod<TKS_BetterInspectCard>().GetSettings<TKS_BetterInspectCardSettings>().debugPrint)
            {
                Log.Message(message);
            }
        }


        public TKS_BetterInspectCard(ModContentPack content) : base(content)
        {
            this.settings = GetSettings<TKS_BetterInspectCardSettings>();
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

    [HarmonyPatch(typeof(Plant))]
    internal static class Plant_Patches
    {

        [HarmonyPatch(typeof(Plant), "GetInspectString")]
        [HarmonyPostfix]
        public static void GetInspectStringy_Postfix(ref string __result, Plant __instance)
        {
            TKS_BetterInspectCard.DebugMessage("running inspect string postfix for " + __instance);

            __result = Utilities.AdjustInspectString(__result, __instance);

        }
    }

    [HarmonyPatch(typeof(DeadPlant))]
    internal static class DeadPlant_Patches
    {

        [HarmonyPatch(typeof(DeadPlant), "GetInspectString")]
        [HarmonyPostfix]
        public static void GetInspectStringy_Postfix(ref string __result, DeadPlant __instance)
        {
            TKS_BetterInspectCard.DebugMessage("running inspect string postfix for " + __instance);

            __result = Utilities.AdjustInspectString(__result, __instance);

        }
    }

    [HarmonyPatch(typeof(ThingWithComps))]
    internal static class ThingWithComps_Patches
    {

        [HarmonyPatch(typeof(ThingWithComps), "GetInspectString")]
        [HarmonyPostfix]
        public static void GetInspectString_Postfix(ref string __result, ThingWithComps __instance)
        {
            
            if (__instance.GetType()==typeof(Pawn))
            {
                //dont do pawns
                return;
            }
            

            TKS_BetterInspectCard.DebugMessage("running inspect string postfix for " + __instance);

            __result = Utilities.AdjustInspectString(__result, __instance);

        }
    }
}

