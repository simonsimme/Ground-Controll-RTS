using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Strike airframes that go up full of one store: a Darkreach or Alkyon loaded with Piledrivers or
/// PAB-250LRs, GBM-500LR glide drones for convoys and bases on the strike jets, and GPO-N once the
/// faction holds warheads. Scoring pylon by pylon alone gives every bomber the same load.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>How often a strike buy of a listed airframe rolls one of these instead of its usual loadout.</summary>
    private const float ThemedLoadoutShare = 0.5f;

    /// <summary>How often a Darkreach with the warheads for it is offered nuclear Piledrivers at all,
    /// so the HE ones stay its usual load.</summary>
    private const float NuclearPiledriverShare = 0.33f;

    /// <summary>Matched on store names with the punctuation taken out, because which spelling the
    /// asset uses ("GBM-500LR", "GBM 500 LR") is not in the decompile. Logged on every launch.</summary>
    private static readonly LoadoutTheme[] LoadoutThemes =
    {
        new("Piledrivers", "PILEDRIVER", convoy: false, nuclear: false, "Darkreach", "Alkyon") { Fillers = new[] { "TUSKOB" } },
        new("Nuclear Piledrivers", "PILEDRIVER", convoy: false, nuclear: true, "Darkreach"),
        new("PAB-250LRs", "PAB250LR", convoy: false, nuclear: false, "Darkreach", "Alkyon") { Fillers = new[] { "TUSKOB", "PILEDRIVER" } },
        new("GBM-500LRs", "GBM500LR", convoy: true, nuclear: false, "Darkreach", "Alkyon", "Ifrit", "Vortex", "Revoker"),
        new("GPO-Ns", "GPON", convoy: false, nuclear: true, "Darkreach", "Alkyon", "Ifrit"),
    };

    private static readonly Dictionary<WeaponMount, string> MountKeys = new();
    private static readonly List<LoadoutTheme> ThemeCandidates = new();
    private static readonly List<Loadout> ThemeLoadouts = new();
    private static readonly List<string> ThemeFillers = new();
    private static readonly HashSet<string> ThemeMisses = new();

    /// <summary>What <see cref="BuildThemedLoadout"/> last put on the pylons its theme's store does
    /// not fit, as a label suffix, or empty.</summary>
    private static string lastFiller = string.Empty;

    /// <summary>
    /// One of <see cref="LoadoutThemes"/> this airframe can carry for the job, at random, or false.
    /// Bombing runs take every theme; close support (the convoys) only the convoy ones.
    /// </summary>
    private static bool TryBuildThemedLoadout(
        AircraftDefinition definition, FactionHQ hq, Airbase airbase, bool bombing, out Loadout? loadout, out string label)
    {
        loadout = null;
        label = string.Empty;
        if (Random.value >= ThemedLoadoutShare)
        {
            return false;
        }

        ThemeCandidates.Clear();
        ThemeLoadouts.Clear();
        ThemeFillers.Clear();
        for (int i = 0; i < LoadoutThemes.Length; i++)
        {
            LoadoutTheme theme = LoadoutThemes[i];
            if ((!bombing && !theme.Convoy) || !theme.Fits(definition) || (theme.Nuclear && !MissionManager.AllowTactical())
                || (theme.Nuclear && theme.Store == "PILEDRIVER" && Random.value >= NuclearPiledriverShare))
            {
                continue;
            }

            if (BuildThemedLoadout(definition, hq, airbase, theme) is Loadout built)
            {
                ThemeCandidates.Add(theme);
                ThemeLoadouts.Add(built);
                ThemeFillers.Add(lastFiller);
            }
            else if (ThemeMisses.Add(definition.unitName + "|" + theme.Label))
            {
                // Store names are asset data: this line is how a renamed store shows up.
                CommanderPlugin.Log.LogInfo($"Loadout theme: {definition.unitName} can carry no {theme.Label} at {airbase.name}.");
            }
        }

        if (ThemeCandidates.Count == 0)
        {
            return false;
        }

        int pick = Random.Range(0, ThemeCandidates.Count);
        loadout = ThemeLoadouts[pick];
        label = ThemeCandidates[pick].Label + ThemeFillers[pick];
        return true;
    }

    /// <summary>
    /// The theme's store on every pylon that takes it and nothing on the rest, or null when no pylon
    /// takes it here. Nothing else, because one close-support store aboard makes <c>PickStrikeMode</c>
    /// fly CAS, which never fires a Piledriver. A nuclear store is offered off the hardpoint's whole option list,
    /// against the faction's warheads: the airbase filter also asks for a player rank, which an AI
    /// pilot does not have, while the base game's own AI loadout check counts warheads only.
    /// </summary>
    private static Loadout? BuildThemedLoadout(AircraftDefinition definition, FactionHQ hq, Airbase airbase, LoadoutTheme theme)
    {
        HardpointSet[]? sets = definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        if (sets == null)
        {
            return null;
        }

        Loadout loadout = new();
        for (int i = 0; i < sets.Length; i++)
        {
            loadout.weapons.Add(null!);
        }

        int warheads = theme.Nuclear ? hq.GetWarheadAvailableForAI() : 0;
        int matched = 0;
        for (int i = 0; i < sets.Length; i++)
        {
            WeaponChecker.GetAvailableWeaponsNonAlloc(null!, sets[i], airbase, hq, allowEmpty: false, LegalMounts);
            WeaponMount? pick = null;
            if (theme.Nuclear)
            {
                List<WeaponMount> options = sets[i].weaponOptions;
                for (int m = 0; m < options.Count && pick == null; m++)
                {
                    WeaponMount option = options[m];
                    int rounds = option?.info != null ? sets[i].hardpoints.Count * option.ammo : 0;
                    if (option?.info != null && option.info.nuclear && rounds <= warheads
                        && WeaponChecker.MountAllowedHQ(option, hq) && IsThemeStore(option, theme))
                    {
                        pick = option;
                        warheads -= rounds;
                    }
                }
            }
            else
            {
                for (int m = 0; m < LegalMounts.Count && pick == null; m++)
                {
                    // Never a nuclear store on a conventional theme: "PILEDRIVER" also matches the nuclear round.
                    if (LegalMounts[m]?.info != null && !LegalMounts[m].info.nuclear && IsThemeStore(LegalMounts[m], theme))
                    {
                        pick = LegalMounts[m];
                    }
                }
            }

            if (pick == null)
            {
                continue;
            }

            loadout.weapons[i] = pick;
            if (sets[i].BlockedByOtherHardpoint(loadout))
            {
                loadout.weapons[i] = null!;
            }
            else
            {
                matched++;
            }
        }

        lastFiller = matched > 0 ? FillEmptyPylons(sets, airbase, hq, theme, loadout) : string.Empty;
        return matched > 0 ? loadout : null;
    }

    /// <summary>
    /// The pylons a bombing theme's store does not fit get one of its fillers, picked at random, or
    /// the other when this airframe takes none of the first, so no pylon goes up empty. Both are
    /// strategic stores, which is why
    /// <c>PickStrikeMode</c> flies such a load as STRATEGIC STRIKE: CAS would never fire them.
    /// </summary>
    private static string FillEmptyPylons(HardpointSet[] sets, Airbase airbase, FactionHQ hq, LoadoutTheme theme, Loadout loadout)
    {
        string[] fillers = theme.Fillers;
        int first = fillers.Length > 0 ? Random.Range(0, fillers.Length) : 0;
        for (int f = 0; f < fillers.Length; f++)
        {
            string store = fillers[(first + f) % fillers.Length];
            string? name = null;
            for (int i = 0; i < sets.Length; i++)
            {
                if (loadout.weapons[i] != null)
                {
                    continue;
                }

                WeaponChecker.GetAvailableWeaponsNonAlloc(null!, sets[i], airbase, hq, allowEmpty: false, LegalMounts);
                for (int m = 0; m < LegalMounts.Count && loadout.weapons[i] == null; m++)
                {
                    WeaponMount mount = LegalMounts[m];
                    if (mount?.info == null || mount.info.nuclear || !MountKey(mount).Contains(store))
                    {
                        continue;
                    }

                    loadout.weapons[i] = mount;
                    if (sets[i].BlockedByOtherHardpoint(loadout))
                    {
                        loadout.weapons[i] = null!;
                    }
                    else
                    {
                        name ??= mount.info.weaponName;
                    }
                }
            }

            if (name != null)
            {
                return $" and {name}s";
            }
        }

        return string.Empty;
    }

    private static bool IsThemeStore(WeaponMount mount, LoadoutTheme theme)
    {
        return MountKey(mount).Contains(theme.Store);
    }

    private static string MountKey(WeaponMount mount)
    {
        if (!MountKeys.TryGetValue(mount, out string key))
        {
            key = Squash(mount.mountName) + "|" + Squash(mount.jsonKey) + "|" + Squash(mount.info.weaponName) + "|" + Squash(mount.info.shortName);
            MountKeys[mount] = key;
        }

        return key;
    }

    private static string Squash(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        System.Text.StringBuilder squashed = new(text!.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                squashed.Append(char.ToUpperInvariant(c));
            }
        }

        return squashed.ToString();
    }

    private sealed class LoadoutTheme
    {
        private readonly string[] airframes;

        internal LoadoutTheme(string label, string store, bool convoy, bool nuclear, params string[] airframes)
        {
            Label = label;
            Store = store;
            Convoy = convoy;
            Nuclear = nuclear;
            this.airframes = airframes;
        }

        internal string Label { get; }

        internal string Store { get; }

        /// <summary>Also flown at the convoys on close support, not only at bases.</summary>
        internal bool Convoy { get; }

        internal bool Nuclear { get; }

        /// <summary>Stores for the pylons this theme's own store does not fit, squashed like <see cref="Store"/>.</summary>
        internal string[] Fillers { get; set; } = System.Array.Empty<string>();

        internal bool Fits(AircraftDefinition definition)
        {
            for (int i = 0; i < airframes.Length; i++)
            {
                if (definition.unitName.IndexOf(airframes[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
