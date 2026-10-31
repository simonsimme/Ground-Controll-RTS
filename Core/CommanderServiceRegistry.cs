using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace GroundControlRts;

/// <summary>
/// Which missions a service is allowed to run on. Core services run everywhere; advanced
/// services (production, air command, supply, naval, SAM work) only run once
/// <see cref="CommanderFeatureGate"/> says so.
/// </summary>
internal enum CommanderTier
{
    Core,
    Advanced,
}

/// <summary>
/// Holds every commander service and dispatches the lifecycle by interface.
/// <para>
/// Registration order is execution order for every phase, so the order of the Register calls
/// in <see cref="CommanderModeController"/> is the whole schedule — read it top to bottom.
/// Services are bucketed once at registration, so no per-frame type tests happen here.
/// A service that throws is reported and the rest of the phase still runs.
/// </para>
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A service runs base-game code that can throw anything; it is logged and must not stop the services registered after it.")]
internal sealed class CommanderServiceRegistry
{
    private readonly List<Gated<ICommanderActivate>> activate = new();
    private readonly List<ICommanderDeactivate> deactivate = new();
    private readonly List<Gated<ICommanderTickActive>> tickActive = new();
    private readonly List<Gated<ICommanderTickPersistent>> tickPersistent = new();
    private readonly List<ICommanderResetSession> resetSession = new();
    private readonly HashSet<string> reportedFailures = new();

    /// <summary>How many service calls have thrown. Read by the self-check.</summary>
    internal int Failures { get; private set; }

    /// <summary>Off only in the self-check, whose probe throws on purpose.</summary>
    internal bool LogFailures { get; set; } = true;

    /// <summary>
    /// Registers a service under a tier and returns it, so construction and registration stay
    /// one statement. A service lands in a phase list only if it implements that phase's
    /// interface.
    /// </summary>
    internal T Register<T>(T service, CommanderTier tier = CommanderTier.Core)
        where T : class
    {
        bool advanced = tier == CommanderTier.Advanced;
        if (service is ICommanderActivate a)
        {
            activate.Add(new Gated<ICommanderActivate>(a, advanced));
        }

        if (service is ICommanderDeactivate d)
        {
            deactivate.Add(d);
        }

        if (service is ICommanderTickActive t)
        {
            tickActive.Add(new Gated<ICommanderTickActive>(t, advanced));
        }

        if (service is ICommanderTickPersistent p)
        {
            tickPersistent.Add(new Gated<ICommanderTickPersistent>(p, advanced));
        }

        if (service is ICommanderResetSession r)
        {
            resetSession.Add(r);
        }

        return service;
    }

    internal void Activate(bool advancedEnabled)
    {
        for (int i = 0; i < activate.Count; i++)
        {
            Gated<ICommanderActivate> entry = activate[i];
            if (entry.Advanced && !advancedEnabled)
            {
                continue;
            }

            try
            {
                entry.Service.Activate();
            }
            catch (Exception ex)
            {
                Report(entry.Service, "Activate", ex);
            }
        }
    }

    /// <summary>Activates only the advanced tier, for a mid-session manual unlock.</summary>
    internal void ActivateAdvanced()
    {
        for (int i = 0; i < activate.Count; i++)
        {
            Gated<ICommanderActivate> entry = activate[i];
            if (entry.Advanced)
            {
                try
                {
                    entry.Service.Activate();
                }
                catch (Exception ex)
                {
                    Report(entry.Service, "Activate", ex);
                }
            }
        }
    }

    /// <summary>Tears every tier down, gated or not — the gate may have flipped since activation.</summary>
    internal void Deactivate()
    {
        for (int i = 0; i < deactivate.Count; i++)
        {
            try
            {
                deactivate[i].Deactivate();
            }
            catch (Exception ex)
            {
                Report(deactivate[i], "Deactivate", ex);
            }
        }
    }

    internal void TickActive(bool advancedEnabled)
    {
        for (int i = 0; i < tickActive.Count; i++)
        {
            Gated<ICommanderTickActive> entry = tickActive[i];
            if (entry.Advanced && !advancedEnabled)
            {
                continue;
            }

            try
            {
                entry.Service.TickActive();
            }
            catch (Exception ex)
            {
                Report(entry.Service, "TickActive", ex);
            }
        }
    }

    internal void TickPersistent(bool advancedEnabled)
    {
        for (int i = 0; i < tickPersistent.Count; i++)
        {
            Gated<ICommanderTickPersistent> entry = tickPersistent[i];
            if (entry.Advanced && !advancedEnabled)
            {
                continue;
            }

            try
            {
                entry.Service.TickPersistent();
            }
            catch (Exception ex)
            {
                Report(entry.Service, "TickPersistent", ex);
            }
        }
    }

    internal void ResetSession()
    {
        for (int i = 0; i < resetSession.Count; i++)
        {
            try
            {
                resetSession[i].ResetSession();
            }
            catch (Exception ex)
            {
                Report(resetSession[i], "ResetSession", ex);
            }
        }
    }

    // One service throwing must not end the whole phase for the services after it. Logged once per
    // service, phase and exception type, because a tick runs every frame.
    private void Report(object service, string phase, Exception ex)
    {
        Failures++;
        string source = service.GetType().Name + "." + phase;
        if (LogFailures && reportedFailures.Add(source + ":" + ex.GetType().Name))
        {
            CommanderPlugin.Log.LogError($"{source} threw; the other services carried on. Reported once per error type.\n{ex}");
        }
    }

    private readonly struct Gated<T>
        where T : class
    {
        internal readonly T Service;
        internal readonly bool Advanced;

        internal Gated(T service, bool advanced)
        {
            Service = service;
            Advanced = advanced;
        }
    }
}
