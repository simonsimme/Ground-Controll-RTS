using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Keeps the GC RTS map rules out of the mission picker. They are mission tags
/// (<see cref="CommanderMapSettings"/>), and "GC Builders 3" or "GC AI Income 150" would read as
/// categories. Since the game's 0.34 picker, filter chips and rows only show the game's own list of
/// tags (play mode and map, <c>MissionTag._missionSelectTags</c>), so "GC RTS" is added to that list to
/// stay filterable, and the full tag list above the description, which shows every tag, drops ours.
/// </summary>
[HarmonyPatch]
internal static class CommanderMissionPickerPatches
{
    private static readonly MissionTag RtsTag = new() { Tag = CommanderFeatureGate.MissionTag, Color = Color.HSVToRGB(0.1f, 0.35f, 0.8f), SortOrder = 9 };
    private static readonly List<MissionTag> Shown = new();

    [HarmonyPatch(typeof(MissionSelectPanel), nameof(MissionSelectPanel.SetActiveGroup))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SetActiveGroupPrefix()
    {
        if (!MissionTag.IsMissionSelectTag(RtsTag))
        {
            ((List<MissionTag>)AccessTools.Field(typeof(MissionTag), "_missionSelectTags").GetValue(null)).Add(RtsTag);
        }
    }

    /// <summary>
    /// The mod's own missions are Ground Control maps by name and their files carry no tag, so the
    /// filter counted none and drew dark. Only the picker's light copy of the mission gets it: the
    /// tag in the mission itself would make them <see cref="CommanderFeatureGate.IsTaggedMission"/>.
    /// </summary>
    [HarmonyPatch(typeof(MissionTag), nameof(MissionTag.AddAutoTags), new[] { typeof(MissionQuickLoad), typeof(MapLoader) })]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void AddAutoTagsPostfix(MissionQuickLoad mission)
    {
        List<MissionTag> tags = mission.missionSettings.Tags;
        if (CommanderMapSettings.IsGroundControlName(mission.Name) && !tags.Contains(RtsTag))
        {
            tags.Add(RtsTag);
        }
    }

    [HarmonyPatch(typeof(MissionPicker), nameof(MissionPicker.SelectMission))]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SelectMissionPostfix(MissionPicker __instance, MissionTagList ___missionTagList)
    {
        List<MissionTag>? tags = __instance.Mission?.missionSettings?.Tags;
        if (tags == null || ___missionTagList == null || !tags.Exists(tag => CommanderMapSettings.IsHiddenTag(tag.Tag)))
        {
            return;
        }

        Shown.Clear();
        foreach (MissionTag tag in tags)
        {
            if (!CommanderMapSettings.IsHiddenTag(tag.Tag))
            {
                Shown.Add(tag);
            }
        }

        ___missionTagList.UpdateList(Shown);
    }
}
