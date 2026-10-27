namespace MeetingDesktop.Core.Workflows;

/// <summary>How an elevated step came out.</summary>
public enum ElevationOutcome
{
    /// <summary>The elevated process ran and reported success.</summary>
    Succeeded,

    /// <summary>The user dismissed the UAC prompt. Nothing was changed.</summary>
    Declined,

    /// <summary>The elevated process ran but failed.</summary>
    Failed,
}

/// <summary>Outcome and proof that this invocation sent no command. Unknown is fail-closed.</summary>
public sealed record ElevationRunResult(ElevationOutcome Outcome, bool DefinitelyNotDispatched = false);

/// <summary>
/// Runs one of the fixed elevated operations.
/// </summary>
/// <remarks>
/// An interface so the workflows can be tested without raising a UAC prompt,
/// and so the App owns how the relaunch is presented.
/// </remarks>
public interface IElevationBroker
{
    /// <summary>Authenticated report from the most recent run, or null when unavailable.
    /// Paths are display data, never authorization for additional file operations.</summary>
    ElevatedOperationReport? LastReport => null;

    /// <summary>
    /// Relaunches the current executable elevated with <paramref name="argument"/>
    /// - one of the <c>ElevatedOperations</c> tokens - and waits for it.
    /// </summary>
    ElevationOutcome Run(string argument);

    /// <summary>Evidence is scoped to this call; implementations must never claim it after dispatch could begin.</summary>
    ElevationRunResult RunWithEvidence(string argument) => new(Run(argument));
}
