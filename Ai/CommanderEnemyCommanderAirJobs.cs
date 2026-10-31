using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// What each airframe is for, so the enemy's air buy picks by job and not by price. Classes follow
/// the game wiki (nuclearoption.wiki.gg).
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// How well each stock airframe does a job, 0 (not its job) to 3 (what it was built for), keyed on
    /// <c>unitName</c> like <c>CommanderEconomyService.IsCricket</c>. An airframe missing here (a mod's)
    /// is rated off its own asset data by <see cref="GetJobFit"/>.
    /// </summary>
    private static readonly Dictionary<string, (int Fighter, int Cas, int Bombing)> JobFits = new()
    {
        ["FS-12 Revoker"] = (3, 1, 1), // Air superiority, deep strike as an extra.
        ["KR-67 Ifrit"] = (3, 2, 2), // Multirole: air superiority and ground attack equally.
        ["FS-20 Vortex"] = (3, 2, 2), // Multirole stealth fighter.
        ["VT-7 Vagrant"] = (2, 1, 1), // Multirole trainer, a good cheap interceptor.
        ["T/A-30 Compass"] = (1, 2, 1), // Light attack trainer.
        ["CI-22 Cricket"] = (1, 2, 0), // COIN, convoys and air defence.
        ["A-19 Brawler"] = (0, 3, 0), // Close air support.
        ["Alkyon AB-4"] = (0, 0, 3), // Bomber and interdictor: airfields, defended sites, ships.
        ["SFB-81 Darkreach"] = (0, 0, 3), // Strategic bomber, no air-to-air, needs an escort.
        ["EW-25 Medusa"] = (0, 0, 0), // Electronic warfare; only ever bought as the radar picket.
    };

    /// <summary>
    /// How well <paramref name="definition"/> does the job a buy is for: a fighter buy, a strike buy
    /// (close support or bombing, <paramref name="bombing"/>), or any job when <paramref name="role"/>
    /// is null. Rotary airframes are 1 for everything: the helicopters are bought by their own rules.
    /// </summary>
    private static int GetJobFit(AircraftDefinition definition, AirRole? role, bool bombing)
    {
        if (!CommanderAirCommandService.HasPlanePilot(definition))
        {
            return 1;
        }

        if (!JobFits.TryGetValue(definition.unitName, out (int Fighter, int Cas, int Bombing) fit))
        {
            // Not in the wiki table: it fits whatever its own role and stores say, at the lowest rank.
            int surface = GetAirRole(definition) == AirRole.Strike ? 1 : 0;
            int air = GetInterceptorScore(definition) >= InterceptorMinScore ? 1 : 0;
            fit = (air, surface, surface);
        }

        return role switch
        {
            AirRole.Fighter => fit.Fighter,
            AirRole.Strike => bombing ? fit.Bombing : fit.Cas,
            _ => System.Math.Max(fit.Fighter, System.Math.Max(fit.Cas, fit.Bombing)),
        };
    }
}
