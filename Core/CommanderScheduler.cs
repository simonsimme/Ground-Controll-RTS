using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The mod's shared "is this periodic job due yet" clock.
/// </summary>
/// <remarks>
/// Everything here runs on <b>scaled</b> time (<c>Time.time</c>), so pausing the game
/// (<c>Time.timeScale</c> 0, see <c>TimeScaleManager</c>) pauses the mod too, and the CMD panel's
/// 2x/4x buttons speed up mod logic as well. UI polling must NOT use these, or panels freeze while
/// paused; it uses <see cref="IsDueRealtime"/>.
/// </remarks>
internal static class CommanderScheduler
{
    private static int heavyFrame = -1;

    internal static float Stagger(string taskName, float interval, float maximumDelay = -1f)
    {
        return Time.time + StaggerPhase(taskName, interval, maximumDelay);
    }

    internal static float StaggerRealtime(string taskName, float interval, float maximumDelay = -1f)
    {
        return Time.unscaledTime + StaggerPhase(taskName, interval, maximumDelay);
    }

    /// <summary>Game-clock schedule: stops while paused, runs faster at 2x/4x.</summary>
    internal static bool IsDue(ref float nextRun, float interval)
    {
        return Due(ref nextRun, interval, Time.time);
    }

    /// <summary>Wall-clock schedule, for UI refresh throttles that must keep working while paused.</summary>
    internal static bool IsDueRealtime(ref float nextRun, float interval)
    {
        return Due(ref nextRun, interval, Time.unscaledTime);
    }

    /// <summary>
    /// Game-clock schedule for the AI's big reviews, which walk every unit a faction owns and order
    /// hundreds of them: due, and no other such review has run this frame. A job refused here stays
    /// due and runs on the next free frame, so reviews never stack on one frame and stall the host.
    /// </summary>
    internal static bool IsDueHeavy(ref float nextRun, float interval)
    {
        return Time.time >= nextRun && TryClaimHeavyFrame() && Due(ref nextRun, interval, Time.time);
    }

    /// <summary>Claims this frame for one heavy AI job; false if another already has it.</summary>
    internal static bool TryClaimHeavyFrame()
    {
        if (heavyFrame == Time.frameCount)
        {
            return false;
        }

        heavyFrame = Time.frameCount;
        return true;
    }

    /// <summary>
    /// One runnable check on the due/stagger arithmetic, run at plugin load. Which <c>Time</c> field
    /// each clock reads cannot be asserted here — at load the scaled and unscaled clocks read the
    /// same value, so a swap is invisible until something actually pauses; that one is guarded by
    /// the comment on this class, not by code.
    /// </summary>
    internal static void SelfCheck()
    {
        float scaled = 0f;
        float realtime = 0f;
        if (!IsDue(ref scaled, 60f) || !IsDueRealtime(ref realtime, 60f))
        {
            CommanderPlugin.Log.LogError("Scheduler self-check FAILED: a due job at time zero did not fire.");
        }

        if (IsDue(ref scaled, 60f) || IsDueRealtime(ref realtime, 60f))
        {
            CommanderPlugin.Log.LogError("Scheduler self-check FAILED: a job fired twice inside its interval.");
        }

        float phase = StaggerPhase("scheduler.selfcheck", 10f, -1f);
        if (phase is < 0f or > 10f)
        {
            CommanderPlugin.Log.LogError($"Scheduler self-check FAILED: stagger phase {phase} is outside its interval.");
        }
    }

    private static bool Due(ref float nextRun, float interval, float now)
    {
        if (now < nextRun)
        {
            return false;
        }

        nextRun = now + interval;
        return true;
    }

    private static float StaggerPhase(string taskName, float interval, float maximumDelay)
    {
        float delayRange = maximumDelay >= 0f ? Mathf.Min(maximumDelay, interval) : interval;
        uint hash = 2166136261;
        for (int i = 0; i < taskName.Length; i++)
        {
            hash = (hash ^ taskName[i]) * 16777619;
        }

        return (hash & 0xFFFF) / 65535f * delayRange;
    }
}
