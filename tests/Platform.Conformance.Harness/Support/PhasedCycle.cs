using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Platform.Conformance.Harness.Support;

/// <summary>How a phase of a <see cref="PhasedCycle"/> ended.</summary>
public enum PhaseStatus
{
    /// <summary>The phase ran to its end.</summary>
    Passed,

    /// <summary>The phase threw; the tests that need it fail with its message.</summary>
    Failed,

    /// <summary>A prerequisite was missing or the platform's own rules kept the phase from running; the tests that need it are Inconclusive.</summary>
    Inconclusive,

    /// <summary>A phase it needs did not pass, so it never ran.</summary>
    Skipped,
}

/// <summary>The end of one phase.</summary>
/// <param name="Name">Phase name, for example <c>force sleep</c>.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Message">Why it failed, was Inconclusive or was skipped; <c>null</c> when it passed.</param>
/// <param name="Duration">How long it ran.</param>
/// <param name="Cause">For a skipped phase, the first phase that did not pass and kept it from running.</param>
public sealed record PhaseOutcome(string Name, PhaseStatus Status, string? Message, TimeSpan Duration, PhaseOutcome? Cause = null)
{
    /// <summary><c>name: status (duration)</c> and the message, if any.</summary>
    public override string ToString() => $"{Name}: {Status} ({DurationFormat.Human(Duration)}){(Message is null ? string.Empty : $": {Message}")}";
}

/// <summary>One step of a <see cref="PhasedCycle"/>.</summary>
/// <param name="Name">Phase name, unique in the cycle; tests name it in <see cref="PhasedCycle.RequireAsync"/>.</param>
/// <param name="RunAsync">The work; throws to fail, throws a <see cref="ResultStateException"/> with an Inconclusive state to be Inconclusive.</param>
/// <param name="Needs">
/// Earlier phases that must have passed for this one to run; <c>null</c> means every earlier phase. An empty list runs the
/// phase whatever happened before (a wake that must leave the tier up).
/// </param>
public sealed record CyclePhase(string Name, Func<CancellationToken, Task> RunAsync, IReadOnlyList<string>? Needs = null);

/// <summary>
/// Runs an ordered list of phases once, on first demand, in the background, and lets any number of tests wait for the
/// phases they assert on. A phase that fails or is Inconclusive is never retried: the phases that need it are skipped and
/// every test that needs any of them gets a message naming the first phase that did not pass. Used by the shared sleep and
/// wake cycle of a tier (one stop and one start for every test of that tier) and by the shared sandbox tdd rollout.
/// </summary>
public sealed class PhasedCycle
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Task> Running = new();
    private readonly IReadOnlyList<CyclePhase> phases;
    private readonly Dictionary<string, TaskCompletionSource<PhaseOutcome>> outcomes;
    private readonly IClock clock;
    private readonly Action<string> progress;
    private readonly TimeSpan budget;
    private readonly Func<Task>? onEnd;
    private readonly Lock gate = new();
    private Task? run;

    /// <summary>Creates a cycle; nothing runs until <see cref="Start"/> or the first wait.</summary>
    /// <param name="name">Name for messages, for example <c>nonprod sleep and wake cycle</c>.</param>
    /// <param name="phases">Phases in the order they run.</param>
    /// <param name="budget">Longest time the whole cycle may take; the running phase is cancelled after it.</param>
    /// <param name="progress">Receives one line when a phase starts and when it ends; ignored when omitted.</param>
    /// <param name="clock">Time source; the system clock when omitted.</param>
    /// <param name="onEnd">Runs once after the last phase whatever happened (releases what the first phase took); its failure is only reported.</param>
    public PhasedCycle(string name, IReadOnlyList<CyclePhase> phases, TimeSpan budget, Action<string>? progress = null, IClock? clock = null, Func<Task>? onEnd = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Count == 0)
        {
            throw new ArgumentException("A cycle needs at least one phase.", nameof(phases));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phase in phases)
        {
            if (!names.Add(phase.Name))
            {
                throw new ArgumentException($"Phase '{phase.Name}' appears twice.", nameof(phases));
            }

            foreach (var needed in phase.Needs ?? [])
            {
                if (!names.Contains(needed) || needed == phase.Name)
                {
                    throw new ArgumentException($"Phase '{phase.Name}' needs '{needed}', which is not an earlier phase.", nameof(phases));
                }
            }
        }

        Name = name;
        this.phases = phases;
        this.budget = budget;
        this.progress = progress ?? (_ => { });
        this.clock = clock ?? SystemClock.Instance;
        this.onEnd = onEnd;
        outcomes = phases.ToDictionary(phase => phase.Name, _ => new TaskCompletionSource<PhaseOutcome>(TaskCreationOptions.RunContinuationsAsynchronously), StringComparer.Ordinal);
    }

    /// <summary>Name for messages.</summary>
    public string Name { get; }

    /// <summary><c>true</c> once the cycle has started.</summary>
    public bool Started
    {
        get
        {
            lock (gate)
            {
                return run is not null;
            }
        }
    }

    /// <summary>Phase names in order.</summary>
    public IReadOnlyList<string> PhaseNames => phases.Select(phase => phase.Name).ToArray();

    /// <summary>
    /// Starts the cycle unless it runs already. It runs on the thread pool and keeps the starting test's execution context
    /// only so that its progress lines reach the log (NUnit drops <c>TestContext.Progress</c> lines written outside any test
    /// context); it never reads that test's cancellation token or result: its phases get the cycle's own token, so a test
    /// that times out while waiting leaves the cycle running for the others.
    /// </summary>
    /// <returns>The whole cycle; it never faults, every phase records its own outcome.</returns>
    public Task Start()
    {
        lock (gate)
        {
            if (run is null)
            {
                run = Task.Run(RunAllAsync, CancellationToken.None);

                Running.Enqueue(run);
            }

            return run;
        }
    }

    /// <summary>Starts the cycle if needed and waits for one phase to end.</summary>
    /// <param name="phase">Phase name.</param>
    /// <param name="cancellationToken">Cancels the wait, never the cycle.</param>
    public Task<PhaseOutcome> WaitForAsync(string phase, CancellationToken cancellationToken)
    {
        if (!outcomes.TryGetValue(phase, out var outcome))
        {
            throw new ArgumentException($"{Name} has no phase '{phase}' (phases: {string.Join(", ", PhaseNames)}).", nameof(phase));
        }

        Start();
        return outcome.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Waits until every cycle started in this process has ended. <see cref="PlatformTestBase"/> calls it in each fixture's
    /// teardown, so a fixture that started a cycle (or shares one) does not end while the cycle runs: NUnit then never
    /// starts a fixture of another shift (the non-parallel fixtures) while a cycle of the parallel ones still sleeps a tier.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait, never the cycles.</param>
    public static Task WaitForAllStartedAsync(CancellationToken cancellationToken) => Task.WhenAll(Running.ToArray()).WaitAsync(cancellationToken);

    /// <summary>Waits for the whole cycle, when it has started.</summary>
    /// <param name="cancellationToken">Cancels the wait, never the cycle.</param>
    public Task WaitForEndAsync(CancellationToken cancellationToken)
    {
        Task? started;
        lock (gate)
        {
            started = run;
        }

        return started is null ? Task.CompletedTask : started.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for the phases a test asserts on and ends the test when one did not pass: a failed or skipped phase fails
    /// it, an Inconclusive phase makes it Inconclusive, both with a message naming the phase and the cycle.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="needed">Phases the test needs, in any order.</param>
    public async Task RequireAsync(CancellationToken cancellationToken, params string[] needed)
    {
        ArgumentNullException.ThrowIfNull(needed);
        var ended = new List<PhaseOutcome>();
        foreach (var phase in needed)
        {
            ended.Add(await WaitForAsync(phase, cancellationToken).ConfigureAwait(false));
        }

        var first = ended.Where(outcome => outcome.Status != PhaseStatus.Passed).OrderBy(outcome => IndexOf(outcome.Name)).FirstOrDefault();
        if (first is null)
        {
            return;
        }

        var cause = first.Cause ?? first;
        var message = $"phase '{cause.Name}' of the {Name} {Describe(cause.Status)}: {cause.Message}"
            + (ReferenceEquals(cause, first) ? string.Empty : $" (so phase '{first.Name}' did not run)");
        if (cause.Status == PhaseStatus.Inconclusive)
        {
            throw new InconclusiveException(message);
        }

        throw new AssertionException(message);
    }

    /// <summary>Outcomes of the phases that have ended, in order.</summary>
    public IReadOnlyList<PhaseOutcome> Outcomes =>
        phases.Select(phase => outcomes[phase.Name].Task).Where(task => task.IsCompleted).Select(task => task.Result).ToArray();

    private static string Describe(PhaseStatus status) => status switch
    {
        PhaseStatus.Failed => "failed",
        PhaseStatus.Inconclusive => "was inconclusive",
        _ => "did not run",
    };

    private int IndexOf(string phase)
    {
        for (var index = 0; index < phases.Count; index++)
        {
            if (phases[index].Name == phase)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private async Task RunAllAsync()
    {
        using var cancellation = new CancellationTokenSource(budget);
        var ended = new Dictionary<string, PhaseOutcome>(StringComparer.Ordinal);
        var cycleStarted = clock.UtcNow;
        progress($"{Name}: started ({string.Join(" -> ", PhaseNames)})");
        foreach (var phase in phases)
        {
            var needs = phase.Needs ?? ended.Keys.ToArray();
            var blocker = needs.Select(needed => ended[needed]).FirstOrDefault(outcome => outcome.Status != PhaseStatus.Passed);
            PhaseOutcome outcome;
            if (blocker is not null)
            {
                var cause = blocker.Cause ?? blocker;
                outcome = new PhaseOutcome(phase.Name, PhaseStatus.Skipped, $"not run because phase '{cause.Name}' {Describe(cause.Status)}: {cause.Message}", TimeSpan.Zero, cause);
            }
            else
            {
                outcome = await RunPhaseAsync(phase, cancellation.Token).ConfigureAwait(false);
            }

            ended[phase.Name] = outcome;
            progress($"{Name}: phase {outcome}");
            outcomes[phase.Name].TrySetResult(outcome);
        }

        if (onEnd is not null)
        {
            try
            {
                await onEnd().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                progress($"{Name}: ending failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        progress($"{Name}: done in {DurationFormat.Human(clock.UtcNow - cycleStarted)}");
    }

    private async Task<PhaseOutcome> RunPhaseAsync(CyclePhase phase, CancellationToken cancellationToken)
    {
        var started = clock.UtcNow;
        progress($"{Name}: phase {phase.Name}: started");
        try
        {
            await phase.RunAsync(cancellationToken).ConfigureAwait(false);
            return new PhaseOutcome(phase.Name, PhaseStatus.Passed, null, clock.UtcNow - started);
        }
        catch (ResultStateException ex) when (ex.ResultState.Status == TestStatus.Inconclusive || ex.ResultState.Status == TestStatus.Skipped)
        {
            return new PhaseOutcome(phase.Name, PhaseStatus.Inconclusive, ex.Message, clock.UtcNow - started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PhaseOutcome(phase.Name, PhaseStatus.Failed, $"cancelled: the {Name} ran out of its {DurationFormat.Human(budget)} budget", clock.UtcNow - started);
        }
        catch (Exception ex)
        {
            return new PhaseOutcome(phase.Name, PhaseStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", clock.UtcNow - started);
        }
    }
}

/// <summary>Writes the progress lines of shared cycles: <c>progress: &lt;text&gt;</c> through <see cref="ConformanceProgress.Write"/>.</summary>
public static class CycleProgress
{
    /// <summary>Writes one line.</summary>
    /// <param name="text">Text after the <c>progress:</c> prefix.</param>
    public static void Write(string text) => ConformanceProgress.Write($"{ProgressFormat.Prefix} {text}");
}
