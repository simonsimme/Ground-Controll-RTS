using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private static readonly FieldInfo? JammingRangeFalloffField = typeof(JammingPod).GetField(
        "rangeFalloff", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The weapon picker's row text, built with the catalogue: it walks prefabs and the
    /// pylon search, and OnGUI asks for every row several times a frame.</summary>
    private readonly Dictionary<WeaponMount, string> weaponLabels = new();

    private string launchWarning = string.Empty;
    private float nextLaunchWarningAt;

    private enum SpecialAirSystem
    {
        None,
        Radar,
        RadarJammer,
    }

    /// <summary>
    /// Why the sortie on screen will not do what it is being bought for, or empty. It warns rather
    /// than refuses: an expert may want an unarmed decoy. Rebuilt a few times a second, because it
    /// walks the depots and OnGUI asks every frame.
    /// </summary>
    internal string LaunchWarning
    {
        get
        {
            if (Time.unscaledTime >= nextLaunchWarningAt)
            {
                nextLaunchWarningAt = Time.unscaledTime + 0.25f;
                launchWarning = BuildLaunchWarning();
            }

            return launchWarning;
        }
    }

    internal string? SelectedCargoWeaponKey =>
        SelectedCargoWeapon != null ? GetWeaponTypeKey(SelectedCargoWeapon) : null;

    internal string? SelectedPrimaryWeaponKey =>
        SelectedPrimaryWeapon != null ? GetWeaponTypeKey(SelectedPrimaryWeapon) : null;

    internal string? SelectedSecondaryWeaponKey =>
        SelectedSecondaryWeapon != null ? GetWeaponTypeKey(SelectedSecondaryWeapon) : null;

    /// <summary>
    /// What the stores on <paramref name="loadout"/> cost, priced the way the base game prices a
    /// player's loadout (<c>WeaponChecker.GetLoadoutFullValue</c>): every hardpoint in a set carries
    /// the mount's empty cost plus <c>costPerRound</c> times its rounds. Munitions cost money in the
    /// base game, so a launch pays for what it carries, whether the airframe was bought or stocked.
    /// </summary>
    internal static float GetMunitionsCost(AircraftDefinition definition, Loadout? loadout)
    {
        // A draft has no money: a drafted airframe's first load was part of its price, and every
        // reload after that comes off the ammunition shelf.
        if (CommanderDraftService.IsDraftMission)
        {
            return 0f;
        }

        HardpointSet[]? sets = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets
            : null;
        if (loadout == null || sets == null)
        {
            return 0f;
        }

        float cost = 0f;
        for (int i = 0; i < loadout.weapons.Count && i < sets.Length; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            float perHardpoint = mount.emptyCost + (mount.info != null ? mount.info.costPerRound * mount.ammo : 0f);
            cost += sets[i].hardpoints.Count * perHardpoint;
        }

        return Mathf.Max(0f, cost);
    }

    /// <summary>
    /// The heading the weapon dropdown files a store under: 0 air, 1 ground, 2 other (radar, jammers,
    /// cargo, the sling hook). Read off the store's own effectiveness numbers, so a gun or missile that
    /// does both goes wherever it is stronger.
    /// </summary>
    internal static int GetMunitionSection(WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        if (info == null || info.jammer || IsCargoMount(mount) || GetSpecialAirSystem(mount) != SpecialAirSystem.None)
        {
            return 2;
        }

        float air = info.effectiveness.antiAir;
        float ground = info.effectiveness.antiSurface;
        if (air > 0.05f && air >= ground)
        {
            return 0;
        }

        return ground > 0.05f ? 1 : 2;
    }

    /// <summary>Cargo and troop pods: carried, never scored as a weapon.</summary>
    internal static bool IsCargoMount(WeaponMount? mount)
    {
        return mount != null
            && (mount.Cargo || mount.Troops || mount.info?.cargo == true || mount.info?.troops == true);
    }

    internal static string GetHardpointButtonLabel(AirMissionOption option, AirHardpointGroup group)
    {
        string mount = group.SelectedMount == null ? "EMPTY" : GetMountName(group.SelectedMount);
        string mirror = group.PhysicalMountCount > 1 ? $"  [MIRRORED x{group.PhysicalMountCount}]" : string.Empty;
        string excludes = GetConflictSummary(option, group);
        return $"{group.Label}{mirror}\n{mount}{excludes}";
    }

    internal static string GetHardpointMountLabel(WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        List<string> tags = new();
        if (IsCargoMount(mount))
        {
            tags.Add("CARGO");
        }

        if (mount.radar || mount.prefab?.GetComponentInChildren<Radar>(true) != null)
        {
            tags.Add("RADAR");
        }

        if (info?.jammer == true)
        {
            tags.Add("ECM");
        }

        if (info != null && IsAradWeapon(info))
        {
            tags.Add("ARAD");
        }

        if (info?.effectiveness.antiAir > 0.05f)
        {
            tags.Add("A/A");
        }

        if (info?.effectiveness.antiSurface > 0.05f)
        {
            tags.Add("A/G");
        }

        string suffix = tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : string.Empty;
        return GetMountName(mount) + suffix;
    }

    internal string GetMissionWeaponLabel(WeaponMount mount)
    {
        return weaponLabels.TryGetValue(mount, out string label) ? label : GetWeaponTypeName(mount);
    }

    /// <summary>The PYLONS tab: one set of hardpoints by hand, -1 empties it. Clashing sets are
    /// emptied, the way the game's own loadout screen does it.</summary>
    internal void SelectHardpointMount(int groupIndex, int mountIndex)
    {
        AirMissionOption? option = SelectedOption;
        if (option != null)
        {
            option.Preset = null;
            SelectHardpointMount(option, groupIndex, mountIndex);
        }
    }

    private static AirMissionOption? CreateVariableLoadoutOption(
        AircraftDefinition definition,
        FactionHQ hq,
        AirCommandMode mode)
    {
        Aircraft? aircraft = definition.unitPrefab.GetComponent<Aircraft>();
        HardpointSet[]? sets = aircraft?.weaponManager?.hardpointSets;
        if (sets == null || sets.Length == 0)
        {
            return null;
        }

        List<AirHardpointGroup> groups = new();
        for (int i = 0; i < sets.Length; i++)
        {
            List<int> indices = new() { i };
            string label = GetHardpointSetName(sets[i], i);
            if (i + 1 < sets.Length && sets[i + 1].SymmetryWithPrev)
            {
                indices.Add(i + 1);
                if (!string.IsNullOrWhiteSpace(sets[i + 1].SymmetryName))
                {
                    label = sets[i + 1].SymmetryName;
                }

                i++;
            }

            List<WeaponMount> mounts = GetCommonMounts(sets, indices, hq);
            int physicalMounts = 0;
            for (int index = 0; index < indices.Count; index++)
            {
                physicalMounts += Mathf.Max(sets[indices[index]].hardpoints?.Count ?? 0, 1);
            }

            groups.Add(new AirHardpointGroup(label, indices, mounts, physicalMounts));
        }

        AirMissionOption option = new(definition, mode, sets, groups);
        return option;
    }

    private static string GetWeaponKind(WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        if (IsCargoMount(mount))
        {
            return "CARGO";
        }

        if (info?.sling == true)
        {
            return "HOOK";
        }

        if (GetSpecialAirSystem(mount) != SpecialAirSystem.None)
        {
            return "POD";
        }

        if (info == null)
        {
            return string.Empty;
        }

        if (info.gun)
        {
            return "GUN";
        }

        if (info.effectiveness.antiRadar > Mathf.Max(info.effectiveness.antiAir, 0.05f))
        {
            return "ANTI-RADAR";
        }

        if (info.targetRequirements.minRadar > 0f)
        {
            return "RADAR";
        }

        if (info.targetRequirements.minIR > 0f)
        {
            return "HEAT";
        }

        if (info.laserGuided)
        {
            return "LASER";
        }

        if (info.bomb)
        {
            return info.glideBomb ? "GLIDE BOMB" : "BOMB";
        }

        return info.missile ? "MISSILE" : string.Empty;
    }

    /// <summary>Stores of this type the airframe carries with every pylon that takes it filled,
    /// clashing bays respected, whatever is on the pylons right now.</summary>
    private static int GetMaximumCapacity(AirMissionOption option, WeaponMount weapon)
    {
        List<int> candidates = new();
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            if (FindEquivalentMountIndex(option.HardpointGroups[i], weapon) >= 0)
            {
                candidates.Add(i);
            }
        }

        List<int> best = new();
        int bestCapacity = 0;
        FindMaximumWeaponGroupsRecursive(option, weapon, candidates, 0, new List<int>(), 0, ref bestCapacity, ref best);
        return bestCapacity;
    }

    /// <summary>Area missions that pick their weapons by class. RADAR flies its own radar and
    /// AUTOPILOT/STANDBY fire nothing of the mod's choosing, so none of them has a wrong loadout.</summary>
    private static bool HasWeaponRule(AirCommandMode mode)
    {
        return RequiresMissionArea(mode) && mode != AirCommandMode.AwacsJammer;
    }

    /// <summary>Whether a mission of this mode will fire anything on this loadout.</summary>
    private static bool CarriesWeaponFor(Loadout loadout, AirCommandMode mode)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount?.info != null && !IsCargoMount(mount) && IsWeaponEligible(mount.info, mode))
            {
                return true;
            }
        }

        return false;
    }

    private static void ConfigureOptionLoadout(
        AirMissionOption option,
        WeaponMount? primary,
        WeaponMount? secondary,
        WeaponMount? cargo,
        LoadoutBalance balance)
    {
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            option.HardpointGroups[i].Clear();
        }

        if (primary == null)
        {
            FinishLoadout(option, cargo);
            return;
        }

        if (secondary == null)
        {
            SelectGroups(option, primary, FindMaximumWeaponGroups(option, primary, Array.Empty<int>()));
            FinishLoadout(option, cargo);
            return;
        }

        if (balance == LoadoutBalance.Primary)
        {
            List<int> primaryGroups = FindMaximumWeaponGroups(option, primary, Array.Empty<int>());
            SelectGroups(option, primary, primaryGroups);
            SelectGroups(option, secondary, FindMaximumWeaponGroups(option, secondary, primaryGroups));
            FinishLoadout(option, cargo);
            return;
        }

        float targetPrimaryShare = balance == LoadoutBalance.Mixed ? 0.75f : 0.25f;
        int[] allocation = FindBalancedWeaponAllocation(option, primary, secondary, targetPrimaryShare);
        bool hasPrimary = Array.IndexOf(allocation, 1) >= 0;
        bool hasSecondary = Array.IndexOf(allocation, 2) >= 0;
        if (!hasPrimary || !hasSecondary)
        {
            List<int> primaryGroups = FindMaximumWeaponGroups(option, primary, Array.Empty<int>());
            SelectGroups(option, primary, primaryGroups);
            SelectGroups(option, secondary, FindMaximumWeaponGroups(option, secondary, primaryGroups));
            FinishLoadout(option, cargo);
            return;
        }

        for (int i = 0; i < allocation.Length; i++)
        {
            if (allocation[i] == 1)
            {
                SelectEquivalentMount(option.HardpointGroups[i], primary);
            }
            else if (allocation[i] == 2)
            {
                SelectEquivalentMount(option.HardpointGroups[i], secondary);
            }
        }

        FinishLoadout(option, cargo);
    }

    /// <summary>
    /// Cargo goes on last, into whatever bays the weapons did not claim, using the same
    /// "fill every group that will take this and does not clash" pass the secondary weapon uses.
    /// </summary>
    private static void FinishLoadout(AirMissionOption option, WeaponMount? cargo)
    {
        if (cargo != null)
        {
            List<int> claimed = new();
            for (int i = 0; i < option.HardpointGroups.Count; i++)
            {
                if (option.HardpointGroups[i].SelectedMount != null)
                {
                    claimed.Add(i);
                }
            }

            SelectGroups(option, cargo, FindMaximumWeaponGroups(option, cargo, claimed));
        }

        EnsureFixedEquipment(option);
    }

    private static void EnsureFixedEquipment(AirMissionOption option)
    {
        EnsureMedusaLaser(option);
        if (CommanderSettings.AirIncludeInternalCannons)
        {
            EnsureInternalCannons(option);
        }
    }

    private static void EnsureInternalCannons(AirMissionOption option)
    {
        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            AirHardpointGroup group = option.HardpointGroups[groupIndex];
            if (group.SelectedMount != null)
            {
                continue;
            }

            bool conflictsWithOtherGroup = false;
            for (int otherIndex = 0; otherIndex < option.HardpointGroups.Count; otherIndex++)
            {
                if (otherIndex != groupIndex
                    && GroupsConflict(option.HardpointSets, group, option.HardpointGroups[otherIndex]))
                {
                    conflictsWithOtherGroup = true;
                    break;
                }
            }

            if (conflictsWithOtherGroup)
            {
                continue;
            }

            for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
            {
                WeaponMount mount = group.Mounts[mountIndex];
                if (mount.info?.gun != true)
                {
                    continue;
                }

                bool dedicatedGunGroup = group.Mounts.Count > 0;
                for (int candidateIndex = 0; candidateIndex < group.Mounts.Count; candidateIndex++)
                {
                    if (group.Mounts[candidateIndex].info?.gun != true)
                    {
                        dedicatedGunGroup = false;
                        break;
                    }
                }

                if (dedicatedGunGroup)
                {
                    group.Select(mountIndex);
                    break;
                }
            }
        }
    }

    private static void EnsureMedusaLaser(AirMissionOption option)
    {
        if (GetAircraftLabel(option.Definition).IndexOf("MEDUSA", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            AirHardpointGroup group = option.HardpointGroups[groupIndex];
            for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
            {
                WeaponMount mount = group.Mounts[mountIndex];
                string identity = string.Join("|", new[]
                {
                    mount.jsonKey,
                    mount.mountName,
                    mount.name,
                    mount.info?.weaponName,
                    mount.info?.shortName,
                });
                if (ContainsWeaponToken(identity, "LASER_EW1", "LASER DESIGNATOR"))
                {
                    group.Select(mountIndex);
                    return;
                }
            }
        }
    }

    private static void SelectGroups(AirMissionOption option, WeaponMount weapon, List<int> groups)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            SelectEquivalentMount(option.HardpointGroups[groups[i]], weapon);
        }
    }

    private static int[] FindBalancedWeaponAllocation(
        AirMissionOption option,
        WeaponMount primary,
        WeaponMount secondary,
        float targetPrimaryShare)
    {
        int count = option.HardpointGroups.Count;
        int[] current = new int[count];
        int[] best = new int[count];
        float bestScore = float.MinValue;
        FindBalancedWeaponAllocationRecursive(
            option, primary, secondary, targetPrimaryShare, 0, current, 0, 0, ref bestScore, best);
        return best;
    }

    private static void FindBalancedWeaponAllocationRecursive(
        AirMissionOption option,
        WeaponMount primary,
        WeaponMount secondary,
        float targetPrimaryShare,
        int index,
        int[] current,
        int primaryStores,
        int secondaryStores,
        ref float bestScore,
        int[] best)
    {
        if (index >= current.Length)
        {
            if (primaryStores <= 0 || secondaryStores <= 0)
            {
                return;
            }

            int total = primaryStores + secondaryStores;
            float share = (float)primaryStores / Mathf.Max(total, 1);
            float score = total - (Mathf.Abs(share - targetPrimaryShare) * total * 2f);
            if (secondaryStores > 0)
            {
                score += 0.01f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                Array.Copy(current, best, current.Length);
            }

            return;
        }

        current[index] = 0;
        FindBalancedWeaponAllocationRecursive(
            option,
            primary,
            secondary,
            targetPrimaryShare,
            index + 1,
            current,
            primaryStores,
            secondaryStores,
            ref bestScore,
            best);

        if (ConflictsWithCurrentSelection(option, index, current))
        {
            return;
        }

        int primaryCapacity = GetGroupWeaponCapacity(option.HardpointGroups[index], primary);
        if (primaryCapacity > 0)
        {
            current[index] = 1;
            FindBalancedWeaponAllocationRecursive(
                option,
                primary,
                secondary,
                targetPrimaryShare,
                index + 1,
                current,
                primaryStores + primaryCapacity,
                secondaryStores,
                ref bestScore,
                best);
        }

        int secondaryCapacity = GetGroupWeaponCapacity(option.HardpointGroups[index], secondary);
        if (secondaryCapacity > 0)
        {
            current[index] = 2;
            FindBalancedWeaponAllocationRecursive(
                option,
                primary,
                secondary,
                targetPrimaryShare,
                index + 1,
                current,
                primaryStores,
                secondaryStores + secondaryCapacity,
                ref bestScore,
                best);
        }

        current[index] = 0;
    }

    private static bool ConflictsWithCurrentSelection(AirMissionOption option, int groupIndex, int[] current)
    {
        AirHardpointGroup candidate = option.HardpointGroups[groupIndex];
        for (int i = 0; i < groupIndex; i++)
        {
            if (current[i] != 0
                && GroupsConflict(option.HardpointSets, candidate, option.HardpointGroups[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static List<int> FindMaximumWeaponGroups(
        AirMissionOption option,
        WeaponMount weaponType,
        IReadOnlyList<int> lockedGroups)
    {
        List<int> candidates = new();
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            AirHardpointGroup group = option.HardpointGroups[i];
            if (group.SelectedMount != null || FindEquivalentMountIndex(group, weaponType) < 0)
            {
                continue;
            }

            bool conflictsWithLocked = false;
            for (int locked = 0; locked < lockedGroups.Count; locked++)
            {
                if (GroupsConflict(option.HardpointSets, group, option.HardpointGroups[lockedGroups[locked]]))
                {
                    conflictsWithLocked = true;
                    break;
                }
            }

            if (!conflictsWithLocked)
            {
                candidates.Add(i);
            }
        }

        // Aircraft have few logical station groups, so an exact search is cheap
        // and avoids combined bays accidentally reducing the primary store count.
        List<int> best = new();
        int bestCapacity = -1;
        FindMaximumWeaponGroupsRecursive(
            option, weaponType, candidates, 0, new List<int>(), 0, ref bestCapacity, ref best);
        return best;
    }

    private static void FindMaximumWeaponGroupsRecursive(
        AirMissionOption option,
        WeaponMount weaponType,
        List<int> candidates,
        int candidateIndex,
        List<int> selected,
        int capacity,
        ref int bestCapacity,
        ref List<int> best)
    {
        if (candidateIndex >= candidates.Count)
        {
            if (capacity > bestCapacity)
            {
                bestCapacity = capacity;
                best = new List<int>(selected);
            }

            return;
        }

        FindMaximumWeaponGroupsRecursive(
            option, weaponType, candidates, candidateIndex + 1, selected, capacity, ref bestCapacity, ref best);

        int groupIndex = candidates[candidateIndex];
        AirHardpointGroup group = option.HardpointGroups[groupIndex];
        for (int i = 0; i < selected.Count; i++)
        {
            if (GroupsConflict(option.HardpointSets, group, option.HardpointGroups[selected[i]]))
            {
                return;
            }
        }

        selected.Add(groupIndex);
        FindMaximumWeaponGroupsRecursive(
            option,
            weaponType,
            candidates,
            candidateIndex + 1,
            selected,
            capacity + GetGroupWeaponCapacity(group, weaponType),
            ref bestCapacity,
            ref best);
        selected.RemoveAt(selected.Count - 1);
    }

    private static void SelectEquivalentMount(AirHardpointGroup group, WeaponMount requested)
    {
        group.Select(FindEquivalentMountIndex(group, requested));
    }

    private static int FindEquivalentMountIndex(AirHardpointGroup group, WeaponMount requested)
    {
        string key = GetWeaponTypeKey(requested);
        int bestIndex = -1;
        float bestPreference = -1f;
        for (int i = 0; i < group.Mounts.Count; i++)
        {
            WeaponMount candidate = group.Mounts[i];
            if (string.Equals(GetWeaponTypeKey(candidate), key, StringComparison.OrdinalIgnoreCase)
                && GetCatalogPreference(candidate) > bestPreference)
            {
                bestIndex = i;
                bestPreference = GetCatalogPreference(candidate);
            }
        }

        return bestIndex;
    }

    private static bool SameMountType(WeaponMount? left, WeaponMount? right)
    {
        return left != null && right != null
            && string.Equals(GetWeaponTypeKey(left), GetWeaponTypeKey(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWeaponTypeKey(WeaponMount mount)
    {
        SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
        if (specialSystem == SpecialAirSystem.RadarJammer)
        {
            return "RADAR JAMMER";
        }

        if (specialSystem == SpecialAirSystem.Radar)
        {
            return "RADAR";
        }

        WeaponInfo? info = mount.info;
        if (!string.IsNullOrWhiteSpace(info?.weaponName))
        {
            return info!.weaponName;
        }

        if (!string.IsNullOrWhiteSpace(info?.shortName))
        {
            return info!.shortName;
        }

        if (!string.IsNullOrWhiteSpace(info?.name))
        {
            return info!.name;
        }

        if (!string.IsNullOrWhiteSpace(mount.name))
        {
            return mount.name;
        }

        return GetMountName(mount);
    }

    private static int GetRackStoreCount(WeaponMount mount)
    {
        return mount.info?.gun == true ? 1 : Mathf.Max(mount.ammo, 1);
    }

    private static float GetCatalogPreference(WeaponMount mount)
    {
        float range = GetSpecialSystemRange(mount);
        return range > 0f ? range : GetRackStoreCount(mount);
    }

    private static int GetGroupWeaponCapacity(AirHardpointGroup group, WeaponMount weaponType)
    {
        int mountIndex = FindEquivalentMountIndex(group, weaponType);
        return mountIndex < 0
            ? 0
            : group.PhysicalMountCount * GetRackStoreCount(group.Mounts[mountIndex]);
    }

    private static string GetWeaponTypeName(WeaponMount mount)
    {
        SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
        if (specialSystem == SpecialAirSystem.RadarJammer)
        {
            return "Radar Jammer";
        }

        if (specialSystem == SpecialAirSystem.Radar)
        {
            return "Radar";
        }

        WeaponInfo? info = mount.info;
        if (!string.IsNullOrWhiteSpace(info?.weaponName))
        {
            return info!.weaponName;
        }

        if (!string.IsNullOrWhiteSpace(info?.shortName))
        {
            return info!.shortName;
        }

        return GetMountName(mount);
    }

    /// <summary>
    /// Every store this set of hardpoints will take, cargo and troop pods included.
    /// </summary>
    /// <remarks>
    /// Cargo is included so a cargo bay can be filled in the ADVANCED tab. It stays out of automatic
    /// loadouts anyway: <c>ScoreMount</c> gives cargo no combat score, so
    /// <c>AutoConfigureRoleLoadout</c> and the role presets never pick it, and the dropdown lists it
    /// below the "not suitable for this mission" separator.
    /// </remarks>
    private static List<WeaponMount> GetCommonMounts(
        HardpointSet[] sets,
        List<int> indices,
        FactionHQ hq)
    {
        List<WeaponMount> result = new();
        List<WeaponMount>? firstOptions = sets[indices[0]].weaponOptions;
        if (firstOptions == null)
        {
            return result;
        }

        for (int mountIndex = 0; mountIndex < firstOptions.Count; mountIndex++)
        {
            WeaponMount? mount = firstOptions[mountIndex];
            if (mount == null || !WeaponChecker.MountAllowedHQ(mount, hq))
            {
                continue;
            }

            bool availableOnEveryMirroredSet = true;
            for (int index = 1; index < indices.Count; index++)
            {
                if (!sets[indices[index]].weaponOptions.Contains(mount))
                {
                    availableOnEveryMirroredSet = false;
                    break;
                }
            }

            if (availableOnEveryMirroredSet && !result.Contains(mount))
            {
                result.Add(mount);
            }
        }

        result.Sort(static (left, right) =>
            string.Compare(GetMountName(left), GetMountName(right), StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void SelectHardpointMount(AirMissionOption option, int groupIndex, int mountIndex)
    {
        if (groupIndex < 0 || groupIndex >= option.HardpointGroups.Count)
        {
            return;
        }

        AirHardpointGroup selected = option.HardpointGroups[groupIndex];
        selected.Select(mountIndex);
        if (selected.SelectedMount == null)
        {
            return;
        }

        for (int otherIndex = 0; otherIndex < option.HardpointGroups.Count; otherIndex++)
        {
            if (otherIndex != groupIndex
                && GroupsConflict(option.HardpointSets, selected, option.HardpointGroups[otherIndex]))
            {
                option.HardpointGroups[otherIndex].Clear();
            }
        }
    }

    private static string GetConflictSummary(AirMissionOption option, AirHardpointGroup group)
    {
        List<string> names = new();
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            AirHardpointGroup other = option.HardpointGroups[i];
            if (!ReferenceEquals(group, other) && GroupsConflict(option.HardpointSets, group, other))
            {
                names.Add(other.Label);
            }
        }

        return names.Count == 0 ? string.Empty : $"  | excludes {string.Join(" / ", names)}";
    }

    private static bool GroupsConflict(HardpointSet[] sets, AirHardpointGroup first, AirHardpointGroup second)
    {
        for (int i = 0; i < first.HardpointIndices.Count; i++)
        {
            for (int j = 0; j < second.HardpointIndices.Count; j++)
            {
                int firstIndex = first.HardpointIndices[i];
                int secondIndex = second.HardpointIndices[j];
                if (ContainsHardpointIndex(sets[firstIndex].precludingHardpointSets, secondIndex)
                    || ContainsHardpointIndex(sets[secondIndex].precludingHardpointSets, firstIndex))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsHardpointIndex(List<byte>? indices, int value)
    {
        return indices != null && value <= byte.MaxValue && indices.Contains((byte)value);
    }

    private static float ScoreMount(WeaponMount mount, AirCommandMode mode)
    {
        Loadout loadout = new();
        loadout.weapons.Add(mount);
        return ScoreLoadout(loadout, mode);
    }

    private static string GetHardpointSetName(HardpointSet set, int index)
    {
        return !string.IsNullOrWhiteSpace(set.name) ? set.name : $"Hardpoint {index + 1}";
    }

    private static string GetMountName(WeaponMount mount)
    {
        if (!string.IsNullOrWhiteSpace(mount.mountName))
        {
            return mount.mountName;
        }

        WeaponInfo? info = mount.info;
        if (info != null && !string.IsNullOrWhiteSpace(info.weaponName))
        {
            return info.weaponName;
        }

        if (!string.IsNullOrWhiteSpace(mount.jsonKey))
        {
            return mount.jsonKey;
        }

        return mount.name;
    }

    private static SpecialAirSystem GetSpecialAirSystem(WeaponMount mount)
    {
        if (mount.info?.jammer == true || mount.prefab?.GetComponentInChildren<JammingPod>(true) != null)
        {
            return SpecialAirSystem.RadarJammer;
        }

        if (mount.radar || mount.prefab?.GetComponentInChildren<Radar>(true) != null)
        {
            return SpecialAirSystem.Radar;
        }

        return SpecialAirSystem.None;
    }

    private static float GetSpecialSystemRange(WeaponMount mount)
    {
        Radar? radar = mount.prefab?.GetComponentInChildren<Radar>(true);
        if (radar != null)
        {
            return radar.GetRadarRange();
        }

        JammingPod? jammer = mount.prefab?.GetComponentInChildren<JammingPod>(true);
        AnimationCurve? falloff = jammer != null ? JammingRangeFalloffField?.GetValue(jammer) as AnimationCurve : null;
        Keyframe[]? keys = falloff?.keys;
        return keys != null && keys.Length > 0 ? keys[keys.Length - 1].time : 0f;
    }

    private static bool ValidateSelectedLoadout(
        AirMissionOption option,
        Loadout loadout,
        Airbase airbase,
        FactionHQ hq,
        out string error)
    {
        // Anything on the pylons is a legal sortie: a transport carries no weapon at all, and a
        // pylon set by hand need not be the primary pick. Whether it suits the mission is the
        // launch warning's job, not a refusal.
        if (!HasUsableLoadout(option))
        {
            error = "This loadout is empty. Pick a preset, or put something on the pylons.";
            return false;
        }

        return ValidateMounts(loadout, option.HardpointSets, airbase, hq, out error);
    }

    /// <summary>
    /// Every pylon may carry what is hung on it, at this field, for this faction. The half of
    /// <see cref="ValidateSelectedLoadout"/> that does not read the window, so the host can run it
    /// on a loadout a guest sent.
    /// </summary>
    private static bool ValidateMounts(
        Loadout loadout, HardpointSet[] sets, Airbase airbase, FactionHQ hq, out string error)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            if (i >= sets.Length)
            {
                error = "That loadout has more pylons than the aircraft.";
                return false;
            }

            HardpointSet set = sets[i];
            if (!WeaponChecker.MountAllowedHardpoint(mount, set)
                || !WeaponChecker.MountAllowedConflict(set, loadout)
                || !WeaponChecker.MountAllowedHQ(mount, hq)
                || !WeaponChecker.MountAllowedAirbase(mount, airbase))
            {
                error = $"{GetMountName(mount)} is not available on {GetHardpointSetName(set, i)} at this airbase.";
                return false;
            }

            // The nuclear gate is the faction's warheads and its DEFCON clearance, not the airbase,
            // and "not available at this airbase" sent players hunting for a base that would take it.
            if (!WeaponChecker.MountAllowedNuclear(mount, set, airbase, null!, hq))
            {
                int needed = set.hardpoints.Count * mount.ammo;
                int held = airbase != null ? airbase.GetWarheads() : 0;
                if (held < needed)
                {
                    error = $"{GetMountName(mount)} needs {needed} enriched warheads and your faction holds {held}. Build a nuclear enrichment plant.";
                    return false;
                }

                // Same score the escalation patch clears each side by: its own on a duel, the mission's elsewhere.
                MissionManager mm = NetworkSceneSingleton<MissionManager>.i;
                bool strategic = mount.info.strategic;
                float score = mm == null ? 0f
                    : CommanderEnemyCommanderService.IsDuelMission && hq != null ? hq.factionScore : mm.currentEscalation;
                float threshold = mm == null ? 0f : strategic ? mm.strategicThreshold : mm.tacticalThreshold;
                error = $"{(strategic ? "Strategic" : "Tactical")} nuclear weapons are not cleared yet. "
                    + $"Your side's score is {score:0} and needs {threshold:0} before {GetMountName(mount)} can be loaded.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The weapons the <em>selected</em> airframe can actually carry, not a pool across every aircraft.
    /// </summary>
    private void BuildWeaponOptions()
    {
        weaponOptions.Clear();
        AirMissionOption? selected = SelectedOption;
        if (selected == null)
        {
            return;
        }

        Dictionary<string, int> catalogIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int groupIndex = 0; groupIndex < selected.HardpointGroups.Count; groupIndex++)
        {
            List<WeaponMount> mounts = selected.HardpointGroups[groupIndex].Mounts;
            for (int mountIndex = 0; mountIndex < mounts.Count; mountIndex++)
            {
                WeaponMount mount = mounts[mountIndex];
                string key = GetWeaponTypeKey(mount);
                if (!catalogIndex.TryGetValue(key, out int existingIndex))
                {
                    catalogIndex[key] = weaponOptions.Count;
                    weaponOptions.Add(mount);
                }
                else if (GetCatalogPreference(mount) > GetCatalogPreference(weaponOptions[existingIndex]))
                {
                    weaponOptions[existingIndex] = mount;
                }
            }
        }

        cargoOptions.Clear();
        for (int i = 0; i < weaponOptions.Count; i++)
        {
            if (IsCargoMount(weaponOptions[i]))
            {
                cargoOptions.Add(weaponOptions[i]);
            }
        }

        cargoOptions.Sort(static (left, right) =>
            string.Compare(GetMountName(left), GetMountName(right), StringComparison.OrdinalIgnoreCase));

        weaponOptions.Sort((left, right) =>
        {
            float leftScore = ScoreMount(left, SelectedMode);
            float rightScore = ScoreMount(right, SelectedMode);
            bool leftSuitable = leftScore > 0f;
            bool rightSuitable = rightScore > 0f;
            if (leftSuitable != rightSuitable)
            {
                return leftSuitable ? -1 : 1;
            }

            int scoreCompare = rightScore.CompareTo(leftScore);
            return scoreCompare != 0
                ? scoreCompare
                : string.Compare(GetMountName(left), GetMountName(right), StringComparison.OrdinalIgnoreCase);
        });

        weaponLabels.Clear();
        for (int i = 0; i < weaponOptions.Count; i++)
        {
            weaponLabels[weaponOptions[i]] = BuildWeaponLabel(selected, weaponOptions[i]);
        }
    }

    /// <summary>
    /// What a player decides a weapon on: "AIM-120  |  RADAR 50 km  |  x6  |  OK FOR AIR SUP". The
    /// count is how many this airframe carries at most; the last part says whether the chosen
    /// mission will ever fire it.
    /// </summary>
    private string BuildWeaponLabel(AirMissionOption option, WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        List<string> parts = new() { GetWeaponTypeName(mount) };
        string kind = GetWeaponKind(mount);
        float range = info?.targetRequirements.maxRange ?? 0f;
        if (range >= 1000f && info?.gun != true && !IsCargoMount(mount))
        {
            kind = $"{kind} {range / 1000f:0} km".Trim();
        }

        if (kind.Length > 0)
        {
            parts.Add(kind);
        }

        int fit = info?.gun == true ? 0 : GetMaximumCapacity(option, mount);
        if (fit > 0)
        {
            parts.Add($"x{fit}");
        }

        if (HasWeaponRule(SelectedMode) && info != null && !IsCargoMount(mount) && IsWeaponEligible(info, SelectedMode))
        {
            parts.Add($"OK FOR {GetModeShortLabel(SelectedMode)}");
        }

        return string.Join("  |  ", parts);
    }

    private string BuildLaunchWarning()
    {
        AirMissionOption? option = SelectedOption;
        if (option == null || !HasUsableLoadout(option))
        {
            return string.Empty;
        }

        if (HasWeaponRule(SelectedMode) && !CarriesWeaponFor(option.BuildLoadout(), SelectedMode))
        {
            return SelectedMode switch
            {
                AirCommandMode.Cas => "GROUND: nothing on this loadout can hit ground targets. It will circle its area without attacking.",
                AirCommandMode.AirGuard => "AIR: nothing on this loadout can hit aircraft. It will circle its area without attacking.",
                AirCommandMode.Arad => "ARAD: this loadout has no anti-radiation missiles. It will circle its area without attacking.",
                AirCommandMode.StrategicStrike => "STRIKE: nothing on this loadout can hit buildings or ships. It will circle its area without attacking.",
                _ => "This loadout carries no weapons. It will fly its area without attacking.",
            };
        }

        // The spawn draws fuel and ordnance off the depots around the field, and is refused when they
        // cannot cover it. Saying so here beats a refusal after the area has been placed.
        AirbaseOption? airbase = SelectedAirbase;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        if (airbase != null && hq != null && stores != null)
        {
            Transform field = airbase.Airbase.center != null ? airbase.Airbase.center : airbase.Airbase.transform;
            if (!stores.CanCoverSpawnSupply(hq, field.GlobalPosition(), option.Definition, out string why))
            {
                return $"The launch will be refused: {why}. Select a supply depot near {airbase.Label} and BUY fuel and ammo.";
            }
        }

        return string.Empty;
    }

    private int FindFirstSuitableWeaponIndex()
    {
        for (int i = 0; i < weaponOptions.Count; i++)
        {
            if (ScoreMount(weaponOptions[i], SelectedMode) > 0f)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Lays the picked primary and secondary onto the selected airframe's pylons. Only the selected one:
    /// the list is not re-sorted, so its row stays under the cursor.
    /// </summary>
    private void ApplySelectedWeapons()
    {
        AirMissionOption? option = SelectedOption;
        if (option == null)
        {
            return;
        }

        option.Preset = null;
        ConfigureOptionLoadout(option, SelectedPrimaryWeapon, SelectedSecondaryWeapon, SelectedCargoWeapon, SelectedLoadoutBalance);
    }

    /// <summary>One stable order for the aircraft list: cheapest first, then by name.</summary>
    private void SortOptions()
    {
        options.Sort(static (left, right) =>
        {
            int price = CommanderEconomyService.PriceOf(left.Definition).CompareTo(CommanderEconomyService.PriceOf(right.Definition));
            return price != 0
                ? price
                : string.Compare(GetAircraftLabel(left.Definition), GetAircraftLabel(right.Definition), StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Keeps a weapon pick across an aircraft change by weapon type rather than by list index: the
    /// catalogue is per airframe now, so index 3 on one aeroplane is a different store on the next.
    /// </summary>
    private void RebuildWeaponSelection(string? primaryKey, string? secondaryKey)
    {
        string? cargoKey = SelectedCargoWeaponKey;
        BuildWeaponOptions();
        SelectedPrimaryWeaponIndex = FindWeaponIndexByKey(primaryKey);
        SelectedSecondaryWeaponIndex = FindWeaponIndexByKey(secondaryKey);
        SelectedCargoWeaponIndex = FindCargoIndexByKey(cargoKey);
        if (SelectedPrimaryWeaponIndex < 0)
        {
            SelectedPrimaryWeaponIndex = FindFirstSuitableWeaponIndex();
        }

        if (SelectedSecondaryWeaponIndex >= 0 && SameMountType(SelectedPrimaryWeapon, SelectedSecondaryWeapon))
        {
            SelectedSecondaryWeaponIndex = -1;
        }

        ApplySelectedWeapons();
    }

    private int FindWeaponIndexByKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int i = 0; i < weaponOptions.Count; i++)
        {
            if (string.Equals(GetWeaponTypeKey(weaponOptions[i]), key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindCargoIndexByKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int i = 0; i < cargoOptions.Count; i++)
        {
            if (string.Equals(GetWeaponTypeKey(cargoOptions[i]), key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
