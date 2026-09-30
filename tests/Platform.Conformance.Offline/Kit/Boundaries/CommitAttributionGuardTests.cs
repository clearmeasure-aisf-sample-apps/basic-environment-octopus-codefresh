using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-011, the commit-attribution guard. Unit: the line matcher on forbidden and allowed samples (Octopus, the pin
/// commit titles and .claude paths never match). Integration: the range check over a temporary git repository with crafted
/// commits (clean range, a trailer, a model identifier, the platform name, an empty range, a range outside the base, a
/// merge commit, a missing base ref, PR text through parameters and environment). Registration: the real repository range.
/// The fixtures are assembled at run time, so no line of this file is itself a forbidden attribution.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class CommitAttributionGuardTests
{
    private const string Author = "A Person <a.person@example.com>";
    private const string PinSubject = "Pin workorders 2.5.757 in prod (Deployments-54634)";

    private string root = null!;
    private string git = null!;

    private static string Trailer => "Co-Authored" + "-By: " + Author;

    private static string ClaudeId => "claude" + "-son" + "net-5-5";

    /// <summary>Creates an empty temporary repository whose branch is main, with an identity and no signing.</summary>
    [SetUp]
    public void CreateRepository()
    {
        git = RequireGit();
        root = Directory.CreateTempSubdirectory("commit-attribution-").FullName;
        Git("init", "--quiet");
        Git("symbolic-ref", "HEAD", "refs/heads/main");
    }

    /// <summary>Deletes the repository (read-only object files included).</summary>
    [TearDown]
    public void DeleteRepository()
    {
        if (root is null)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Each forbidden form is found on its line with the rule that names it, in any letter case.</summary>
    [TestCaseSource(nameof(ForbiddenSamples))]
    [Capability("CAP-KIT-011")]
    public void Should_FindAttribution_ForbiddenSample_NamesTheRule(string text, string rule)
    {
        var matches = CommitAttributionGuard.Find(text);

        matches.ShouldHaveSingleItem().Rule.ShouldBe(rule);
        matches[0].LineNumber.ShouldBe(1);
    }

    /// <summary>The platform name, the pin commit titles, paths and prose never match.</summary>
    [TestCaseSource(nameof(AllowedSamples))]
    [Capability("CAP-KIT-011")]
    public void Should_FindAttribution_AllowedSample_MatchesNothing(string text)
    {
        CommitAttributionGuard.Find(text).ShouldBeEmpty();
    }

    /// <summary>Only the offending lines of a multi-line message (LF or CRLF) are reported, with their line numbers.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_FindAttribution_MultiLineMessage_ReportsOnlyOffendingLines()
    {
        var message = string.Join("\r\n", PinSubject, string.Empty, "Octopus release 2.5.757.", Trailer, "Tested with " + ClaudeId + ".");

        var matches = CommitAttributionGuard.Find(message);

        matches.Select(match => (match.Rule, match.LineNumber)).ShouldBe(
            [(CommitAttributionGuard.CoAuthorTrailer, 4), (CommitAttributionGuard.ModelIdentifier, 5)]);
        matches.ShouldAllBe(match => !match.Line.EndsWith('\r'));
        CommitAttributionGuard.Find(null).ShouldBeEmpty();
        CommitAttributionGuard.Find(string.Empty).ShouldBeEmpty();
    }

    /// <summary>A long offending line is echoed truncated, never whole.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_FindAttribution_LongLine_IsEchoedTruncated()
    {
        var line = Trailer + " " + new string('x', 400);

        CommitAttributionGuard.Find(line).ShouldHaveSingleItem().Line.Length.ShouldBeLessThan(200);
    }

    /// <summary>A range of clean commits (the platform name and a pin title among them) passes and reports the missing PR text.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_CleanCommits_Passes()
    {
        Commit("base");
        var baseSha = Head();
        Git("update-ref", "refs/remotes/origin/main", baseSha);
        Commit("Add the Octopus deploy runbook", "Octopus Deploy runs it. Refs #57.");
        Commit(PinSubject);

        var result = Check();

        result.Findings.ShouldBeEmpty(result.Report());
        result.Skipped.ShouldBeFalse();
        result.Warnings.ShouldContain(warning => warning.Contains(CommitAttributionGuard.PrTitleVariable, StringComparison.Ordinal));
        result.Report().ShouldStartWith("PASS");
    }

    /// <summary>A trailer in the middle of the range fails with the commit's sha7, its subject and the rule.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_TrailerInTheMiddleCommit_FailsNamingShaAndRule()
    {
        Commit("base");
        Git("update-ref", "refs/remotes/origin/main", Head());
        Commit("first clean");
        Commit("second commit", "Body text.", Trailer);
        var offending = Head();
        Commit("third clean");

        var result = Check();

        var finding = result.Findings.ShouldHaveSingleItem();
        finding.Path.ShouldStartWith(offending, Case.Sensitive);
        finding.Path.ShouldContain("second commit");
        finding.Text.ShouldStartWith(CommitAttributionGuard.CoAuthorTrailer, Case.Sensitive);
        result.Report().ShouldStartWith("FAIL");
        result.Report().ShouldContain(offending);
    }

    /// <summary>A model identifier in a commit body fails; a footer of the generated-with form fails.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_ModelIdentifierInBodyAndFooter_Fail()
    {
        Commit("base");
        Git("update-ref", "refs/remotes/origin/main", Head());
        Commit("Change the guard", "Written with " + ClaudeId + " in mind.");
        var identifier = Head();
        Commit("Change the docs", "Generated " + "with [Claude" + " Code](https://example.invalid)");
        var footer = Head();

        var result = Check();

        result.Findings.Select(finding => finding.Path[..7]).ShouldBe([footer, identifier], ignoreOrder: true);
        result.Findings.Select(finding => finding.Text.Split(' ')[0]).ShouldBe(
            [CommitAttributionGuard.AttributionFooter, CommitAttributionGuard.ModelIdentifier], ignoreOrder: true);
    }

    /// <summary>A trailer on a commit at or below the base is outside the range and does not fail.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_OffendingCommitOlderThanTheBase_IsOutsideTheRange()
    {
        Commit("old commit", Trailer);
        Commit("base");
        Git("update-ref", "refs/remotes/origin/main", Head());
        Commit("clean branch commit");

        Check().Findings.ShouldBeEmpty();
    }

    /// <summary>On main itself HEAD equals the base, so the range is empty and passes even with an old offending commit.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_OnMain_EmptyRangePassesWithOldOffenders()
    {
        Commit("old commit", Trailer);
        Commit("tip of main");
        Git("update-ref", "refs/remotes/origin/main", Head());

        var result = Check();

        result.Findings.ShouldBeEmpty(result.Report());
        result.Skipped.ShouldBeFalse();
    }

    /// <summary>A merge commit in the range is read: an offending merge message fails, a clean one passes.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_MergeCommit_MessageIsRead()
    {
        Commit("base");
        Git("update-ref", "refs/remotes/origin/main", Head());
        Git("switch", "--quiet", "-c", "feature");
        Commit("feature work");
        Git("switch", "--quiet", "main");
        Commit("main moved on");
        Git("update-ref", "refs/remotes/origin/main", Head());
        Git("switch", "--quiet", "feature");
        Git("-c", "commit.gpgsign=false", "merge", "--quiet", "--no-ff", "main", "-m", "Merge main", "-m", "Reviewed by " + ClaudeId + ".");
        var merge = Head();

        var result = Check();

        var finding = result.Findings.ShouldHaveSingleItem();
        finding.Path.ShouldStartWith(merge, Case.Sensitive);
        finding.Path.ShouldContain("Merge main");
        finding.Text.ShouldStartWith(CommitAttributionGuard.ModelIdentifier, Case.Sensitive);
    }

    /// <summary>When the base ref does not exist (shallow clone, first push) the range part is skipped, never passed silently.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_MissingBaseRef_IsSkippedNotFailed()
    {
        Commit("only commit", Trailer);

        var result = Check();

        result.Skipped.ShouldBeTrue();
        result.Findings.ShouldBeEmpty();
        result.Absent.ShouldBe("origin/main");
        result.Report().ShouldStartWith("SKIP");
    }

    /// <summary>A missing base ref still checks the PR text that was supplied, and reports that the range was not checked.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_MissingBaseRefWithPrText_ChecksTheTextAndWarns()
    {
        Commit("only commit");

        var clean = Check(prTitle: "Guard: clean title");
        var dirty = Check(prBody: "Body\n" + Trailer);

        clean.Skipped.ShouldBeFalse();
        clean.Findings.ShouldBeEmpty();
        clean.Warnings.ShouldContain(warning => warning.Contains("origin/main", StringComparison.Ordinal));
        dirty.Findings.ShouldHaveSingleItem().Path.ShouldBe("PR body");
    }

    /// <summary>A directory that is no git work tree is skipped.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_NotAGitWorkTree_IsSkipped()
    {
        var plain = Directory.CreateTempSubdirectory("commit-attribution-plain-").FullName;
        try
        {
            var result = CommitAttributionGuard.Check(git, plain, new StubEnvironmentVariables());

            result.Skipped.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    /// <summary>The PR title and body checked through the environment values: a clean pair passes, an offending one fails with the line.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckPrText_ThroughTheEnvironment_PassesCleanAndFailsOffending()
    {
        Commit("base");
        Git("update-ref", "refs/remotes/origin/main", Head());
        Commit("clean commit");

        var clean = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables(
            (CommitAttributionGuard.PrTitleVariable, "Guard: fail PRs with attribution"),
            (CommitAttributionGuard.PrBodyVariable, "Refs #57\n\nOctopus stays allowed.")));
        var dirty = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables(
            (CommitAttributionGuard.PrTitleVariable, "Fine title with " + ClaudeId),
            (CommitAttributionGuard.PrBodyVariable, "Refs #57\n" + Trailer)));

        clean.Findings.ShouldBeEmpty(clean.Report());
        clean.Warnings.ShouldNotContain(warning => warning.Contains("no PR text", StringComparison.Ordinal));
        dirty.Findings.Select(finding => (finding.Path, finding.Line)).ShouldBe([("PR title", 1), ("PR body", 2)]);
    }

    /// <summary>PR text passed as arguments wins over the environment; empty values mean none supplied.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckPrText_ArgumentsWinOverTheEnvironment()
    {
        Commit("base");
        Git("update-ref", "refs/remotes/origin/main", Head());
        var environment = new StubEnvironmentVariables(
            (CommitAttributionGuard.PrTitleVariable, "Title with " + ClaudeId),
            (CommitAttributionGuard.PrBodyVariable, "   "));

        var overridden = CommitAttributionGuard.Check(git, root, environment, prTitle: "A clean title");
        var fromEnvironment = CommitAttributionGuard.Check(git, root, environment);

        overridden.Findings.ShouldBeEmpty();
        fromEnvironment.Findings.ShouldHaveSingleItem().Path.ShouldBe("PR title");
    }

    /// <summary>An explicit range argument and the range variable both replace the default base; the argument wins.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_ExplicitRange_ArgumentAndVariable()
    {
        Commit("base");
        var baseSha = Head();
        Commit("offending", Trailer);
        var offending = Head();
        Commit("clean tip");
        var tip = Head();

        var whole = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables(), $"{baseSha}..{tip}");
        var afterOffender = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables(), $"{offending}..{tip}");
        var fromVariable = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables((CommitAttributionGuard.RangeVariable, $"{baseSha}..{tip}")));
        var argumentWins = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables((CommitAttributionGuard.RangeVariable, $"{baseSha}..{tip}")), $"{offending}..{tip}");

        whole.Findings.ShouldHaveSingleItem().Path.ShouldStartWith(offending, Case.Sensitive);
        afterOffender.Findings.ShouldBeEmpty();
        fromVariable.Findings.ShouldHaveSingleItem();
        argumentWins.Findings.ShouldBeEmpty();
    }

    /// <summary>An explicit range that names an unknown ref is skipped, not failed.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_UnknownExplicitRange_IsSkipped()
    {
        Commit("base");

        var result = CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables(), "no-such-ref..HEAD");

        result.Skipped.ShouldBeTrue();
        result.Absent.ShouldBe("no-such-ref..HEAD");
    }

    /// <summary>The default base follows origin/HEAD, so a repository whose default branch is not main is read against it.</summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_CheckRange_DefaultBranchFromOriginHead_IsTheBase()
    {
        Commit("base");
        Git("update-ref", "refs/remotes/origin/trunk", Head());
        Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");
        Commit("branch commit", Trailer);

        var result = Check();

        result.Findings.ShouldHaveSingleItem().Text.ShouldStartWith(CommitAttributionGuard.CoAuthorTrailer, Case.Sensitive);
    }

    /// <summary>
    /// The commits of the checked-out branch (origin/&lt;default&gt;..HEAD) carry no attribution; on main the range is empty.
    /// The PR text is checked here too when PLATFORM_PR_TITLE and PLATFORM_PR_BODY are set (the feature-loop pre-PR gate).
    /// </summary>
    [Test]
    [Capability("CAP-KIT-011")]
    public void Should_GuardCommitAttribution_RepositoryRange_CarriesNoAttribution()
    {
        var realGit = RequireGit();

        var result = CommitAttributionGuard.Check(realGit, KitToolbox.RepositoryRoot, ProcessEnvironmentVariables.Instance);

        TestContext.Out.Write(result.Report());
        if (result.Skipped)
        {
            // Where env-checks runs (CI=true) the base ref must exist: a skipped range there would let every PR pass unchecked.
            if (KitToolbox.IsCi)
            {
                Assert.Fail("CI=true: the commit range could not be read, so the guard did not run. " + result.Report());
            }

            Assert.Inconclusive(result.Report());
        }

        result.Findings.ShouldBeEmpty(result.Report());
    }

    /// <summary>Forbidden samples: text and the rule that names it.</summary>
    public static IEnumerable<TestCaseData> ForbiddenSamples()
    {
        yield return new TestCaseData(Trailer, CommitAttributionGuard.CoAuthorTrailer).SetArgDisplayNames("trailer");
        yield return new TestCaseData(Trailer.ToUpperInvariant(), CommitAttributionGuard.CoAuthorTrailer).SetArgDisplayNames("trailer upper case");
        yield return new TestCaseData(Trailer.ToLowerInvariant(), CommitAttributionGuard.CoAuthorTrailer).SetArgDisplayNames("trailer lower case");
        yield return new TestCaseData("    " + Trailer, CommitAttributionGuard.CoAuthorTrailer).SetArgDisplayNames("trailer with leading spaces");
        yield return new TestCaseData("Generated " + "with [Claude" + " Code](https://claude.com/claude-code)", CommitAttributionGuard.AttributionFooter).SetArgDisplayNames("footer with link");
        yield return new TestCaseData("generated " + "with claude", CommitAttributionGuard.AttributionFooter).SetArgDisplayNames("footer lower case");
        yield return new TestCaseData("Reach us at noreply" + "@anthropic.com", CommitAttributionGuard.AttributionFooter).SetArgDisplayNames("vendor address");
        yield return new TestCaseData(ClaudeId, CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("dashed identifier");
        yield return new TestCaseData("Reviewed with " + ClaudeId.ToUpperInvariant() + " today", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("identifier in a sentence, upper case");
        yield return new TestCaseData("claude" + "-opus-4-1", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("opus identifier");
        yield return new TestCaseData("claude" + "-fable-5", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("fable identifier");
        yield return new TestCaseData("claude" + "-haiku-4-5", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("haiku identifier");
        yield return new TestCaseData("Claude " + "Sonnet 5.5", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("Claude family with version");
        yield return new TestCaseData("Claude " + "Opus", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("Claude family without version");
        yield return new TestCaseData("Written by claude " + "haiku 4", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("lower case Claude family");
        yield return new TestCaseData("Model: Op" + "us 4.5", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("bare family with version");
        yield return new TestCaseData("Model: Fab" + "le 5", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("bare fable with version");
        yield return new TestCaseData("Model: Son" + "net-5", CommitAttributionGuard.ModelIdentifier).SetArgDisplayNames("bare family, hyphen, version");
    }

    /// <summary>Allowed samples: the platform name, pin titles, paths and prose.</summary>
    public static IEnumerable<TestCaseData> AllowedSamples()
    {
        yield return new TestCaseData("Octopus").SetArgDisplayNames("Octopus");
        yield return new TestCaseData("OCTOPUS").SetArgDisplayNames("OCTOPUS");
        yield return new TestCaseData("Octopus Deploy").SetArgDisplayNames("Octopus Deploy");
        yield return new TestCaseData("octopus-argocd-pin-bot").SetArgDisplayNames("pin bot");
        yield return new TestCaseData("Octopus 2025.1 release notes").SetArgDisplayNames("Octopus with a version");
        yield return new TestCaseData(PinSubject).SetArgDisplayNames("pin title");
        yield return new TestCaseData("Pin workorders 2.5.757 in tdd (Deployments-54632)").SetArgDisplayNames("pin title tdd");
        yield return new TestCaseData("Merge pull request #39 from clearmeasure-aisf-sample-apps/claude/magical-carson-w4c4pn-env-37").SetArgDisplayNames("merge title with claude branch");
        yield return new TestCaseData("Update .claude/skills/feature-loop/SKILL.md and .claude/factory-loop.json").SetArgDisplayNames(".claude paths");
        yield return new TestCaseData("Document the Claude Code settings").SetArgDisplayNames("Claude Code settings");
        yield return new TestCaseData("Refs #57").SetArgDisplayNames("issue reference");
        yield return new TestCaseData("Write a sonnet about deployments").SetArgDisplayNames("word in prose without a version");
        yield return new TestCaseData("A magnum opus of a runbook").SetArgDisplayNames("opus in prose");
        yield return new TestCaseData("Reviewed-by: A Person <a.person@example.com>").SetArgDisplayNames("other trailer");
        yield return new TestCaseData("The Co-Authored trailer is forbidden here").SetArgDisplayNames("trailer mentioned in prose");
        yield return new TestCaseData("Contact noreply@example.com").SetArgDisplayNames("other no-reply address");
    }

    /// <summary>The git executable (git.exe on Windows), or ends the test: Inconclusive locally, failed when <c>CI=true</c>.</summary>
    private static string RequireGit()
    {
        if (GitCli.Find() is { } found)
        {
            return found;
        }

        if (KitToolbox.IsCi)
        {
            Assert.Fail("git not found on PATH (CI=true: env-checks must provide it)");
        }

        Assert.Inconclusive("git not found on PATH; the check runs where git is installed (env-checks)");
        return string.Empty;
    }

    private BoundaryResult Check(string? prTitle = null, string? prBody = null) =>
        CommitAttributionGuard.Check(git, root, new StubEnvironmentVariables(), prTitle: prTitle, prBody: prBody);

    private string Head() => Git("log", "-1", "--format=%h").Trim();

    private void Commit(string subject, params string[] paragraphs)
    {
        var arguments = new List<string> { "-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", subject };
        foreach (var paragraph in paragraphs)
        {
            arguments.Add("-m");
            arguments.Add(paragraph);
        }

        Git([.. arguments]);
    }

    private string Git(params string[] arguments)
    {
        var result = GitCli.Run(git, root, ["-c", "user.name=Test Person", "-c", "user.email=test.person@example.com", .. arguments]);
        result.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output;
    }
}
