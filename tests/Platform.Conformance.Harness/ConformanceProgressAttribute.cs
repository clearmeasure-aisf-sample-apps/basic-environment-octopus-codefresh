using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Execution;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness;

/// <summary>
/// Assembly-level NUnit action that reports each test's start and end as <c>progress:</c> lines, with its position among
/// the tests the run's filter selected, the counts so far, the percent complete and an ETA (<see cref="ProgressFormat"/>),
/// and keeps <c>$CONFORMANCE_PROGRESS_DIR/&lt;assembly&gt;.json</c> current for the pipeline's heartbeat.
/// </summary>
/// <example><code>[assembly: ConformanceProgress]</code></example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ConformanceProgressAttribute : Attribute, ITestAction
{
    /// <summary>Suite label shown in each line, for example <c>offline</c>; none by default.</summary>
    public string? Label { get; set; }

    /// <summary>0 (default) prints every start and end; above 0 prints only an end line at each step of that many percent, and failures.</summary>
    public int EveryPercent { get; set; }

    /// <inheritdoc />
    public ActionTargets Targets => ActionTargets.Test;

    /// <inheritdoc />
    public void BeforeTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        Tracker(test).TestStarted(test.Id, DisplayName(test), Capabilities(test));
    }

    /// <inheritdoc />
    public void AfterTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var outcome = TestContext.CurrentContext.Result.Outcome;
        var counted = outcome.Status switch
        {
            TestStatus.Passed or TestStatus.Warning => ProgressOutcome.Passed,
            TestStatus.Failed => ProgressOutcome.Failed,
            _ => ProgressOutcome.Skipped,
        };
        var text = outcome.Status == TestStatus.Failed && !string.IsNullOrEmpty(outcome.Label) ? outcome.Label : outcome.Status.ToString();
        Tracker(test).TestFinished(test.Id, counted, text);
    }

    /// <summary><c>Class.Method</c>, with the arguments of a test case.</summary>
    /// <param name="test">The test.</param>
    public static string DisplayName(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var className = test.ClassName is { Length: > 0 } fullName ? fullName[(fullName.LastIndexOf('.') + 1)..] : null;
        return className is null ? test.Name : $"{className}.{test.Name}";
    }

    private static IReadOnlyCollection<string> Capabilities(ITest test) =>
        test.Properties[CapabilityAttribute.PropertyName].OfType<string>().ToArray();

    private ProgressTracker Tracker(ITest test) => ConformanceProgress.GetOrCreate(() =>
    {
        var assembly = test.TypeInfo?.Assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(Root(test).Name);
        var directory = Environment.GetEnvironmentVariable(ConformanceProgress.DirectoryVariable);
        var file = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory.Trim(), $"{assembly}.json");
        return new ProgressTracker(assembly, SelectedTests.Count(test), ConformanceProgress.WriteToLog, file, Label, EveryPercent);
    });

    private static ITest Root(ITest test)
    {
        var node = test;
        while (node.Parent is { } parent)
        {
            node = parent;
        }

        return node;
    }
}

/// <summary>
/// Counts the tests an NUnit run will execute: the leaves of the loaded tree that pass the run's filter (the filter the
/// adapter built from <c>dotnet test --filter</c>) and are runnable, an <c>[Explicit]</c> test or fixture only when the
/// filter names it, as NUnit itself decides. The filter is read from the dispatcher's top-level work item, which NUnit
/// does not expose publicly; when that fails the count is unknown and the lines show <c>?</c>.
/// </summary>
public static class SelectedTests
{
    private const string TopLevelWorkItemField = "_topLevelWorkItem";
    private const string SavedWorkItemsField = "_savedWorkItems";

    /// <summary>The number of tests the current run executes in the assembly of <paramref name="test"/>.</summary>
    /// <param name="test">Any test of the run.</param>
    /// <returns>The count; <c>null</c> when the run's filter cannot be read.</returns>
    public static int? Count(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        try
        {
            var filter = RunFilter();
            if (filter is null)
            {
                return null;
            }

            var root = test;
            while (root.Parent is { } parent)
            {
                root = parent;
            }

            return Count(root, filter);
        }
        catch (Exception ex) when (ex is MemberAccessException or InvalidCastException or NullReferenceException or TargetException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The number of leaves below <paramref name="node"/> that <paramref name="filter"/> selects and NUnit runs.</summary>
    /// <param name="node">A test or suite.</param>
    /// <param name="filter">The run's filter.</param>
    public static int Count(ITest node, ITestFilter filter)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.Pass(node) || !(node.RunState == RunState.Runnable || (node.RunState == RunState.Explicit && filter.IsExplicitMatch(node))))
        {
            return 0;
        }

        return node.IsSuite ? node.Tests.Sum(child => Count(child, filter)) : 1;
    }

    private static ITestFilter? RunFilter()
    {
        var dispatcher = TestExecutionContext.CurrentContext.Dispatcher;
        if (dispatcher is null)
        {
            return null;
        }

        // The parallel dispatcher swaps the top-level item while a non-parallel fixture runs and keeps the run's own item
        // at the bottom of its stack; every item carries the same filter, but the bottom one is the run's by construction.
        var top = Field(dispatcher, TopLevelWorkItemField) as WorkItem;
        if (Field(dispatcher, SavedWorkItemsField) is IEnumerable<WorkItem> saved && saved.LastOrDefault() is { } bottom)
        {
            top = bottom;
        }

        return top?.Filter;
    }

    private static object? Field(object target, string name)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field)
            {
                return field.GetValue(target);
            }
        }

        return null;
    }
}
