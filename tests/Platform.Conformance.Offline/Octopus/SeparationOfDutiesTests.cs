using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-004 and CAP-OCT-005, offline half: every app process and starter that inlines platform-sod-guard carries
/// octopus/step-templates/sod-guard.ps1 verbatim, so the guard that the live tests prove is the one every app runs, and
/// the guard decides every row of its decision table as the Bash guard did before the PowerShell conversion. The
/// decision tables run the inline copies of the sandbox process (Prod go/no-go with the creator rule, UAT sign-off
/// without) and the template script itself under the stub Octopus runtime of <see cref="OctopusScriptRunner"/>.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class SeparationOfDutiesTests
{
    private const string Process = ".octopus/apps/sandbox/sandbox/deployment_process.ocl";
    private const string GoNoGo = "Octopus.Action[Prod go/no-go].Output.Manual.";
    private const string UatSignOff = "Octopus.Action[UAT sign-off].Output.Manual.";
    private const string Hotfix = "Octopus.Action[Hotfix justification].Output.Manual.";
    private const string Automation = "AISF-Service-Account";

    /// <summary>Each inline copy of sod-guard.ps1 equals the step-template script.</summary>
    [Test]
    [Capability("CAP-OCT-004")]
    public void Should_InlineSodGuard_EveryAppProcess_EqualsTheTemplateScript()
    {
        var canonical = OctopusRepository.CanonicalLines("sod-guard");
        var copies = OctopusRepository.AppProcesses()
            .SelectMany(file => OctopusRepository.InlineCopies(OctopusRepository.Read(file), "sod-guard").Select(copy => (file, copy)))
            .ToArray();

        copies.ShouldNotBeEmpty("no app process inlines sod-guard.ps1");
        foreach (var (file, copy) in copies)
        {
            copy.ShouldBe(canonical, $"{file}: the inline copy of sod-guard.ps1 differs from octopus/step-templates/sod-guard.ps1");
        }
    }

    /// <summary>
    /// The creator rule of Platform.SoDMode: a different user passes; the creator passes in single-operator mode only
    /// with a reason (and a warning) and never in enforce mode (channel Strict); the UAT sign-off has no creator rule.
    /// </summary>
    [TestCase("sod-guard", "single-operator", "Users-2", "Checked the release notes.", false, "Separation of duties holds for 'Prod go/no-go' (Platform.SoDMode single-operator): answered by bob.", TestName = "{m}(different user)")]
    [TestCase("sod-guard", "single-operator", "Users-1", "  Hotfix INC-7, approved alone  \nsecond line", false, "Single-operator mode: alice created this deployment and answered 'Prod go/no-go'. Reason: Hotfix INC-7, approved alone", TestName = "{m}(same user with a reason)")]
    [TestCase("sod-guard", "single-operator", "Users-1", "   ", true, "Separation of duties (single-operator): alice created this deployment and answered 'Prod go/no-go' without a reason in Notes.", TestName = "{m}(same user without a reason)")]
    [TestCase("sod-guard", "enforce", "Users-1", "Approved.", true, "Separation of duties (enforce): alice created this deployment and answered 'Prod go/no-go'. Another member of the responsible team must answer.", TestName = "{m}(Strict mode, same user)")]
    [TestCase("sod-guard", "ENFORCE", "Users-2", "Approved.", false, "Separation of duties holds for 'Prod go/no-go' (Platform.SoDMode enforce): answered by bob.", TestName = "{m}(Strict mode, different user)")]
    [TestCase("sod-guard", "", "Users-1", "Approved alone.", false, "Separation of duties holds for 'Prod go/no-go' (Platform.SoDMode single-operator): answered by alice.", TestName = "{m}(mode unset means single-operator)")]
    [TestCase("sod-guard", "strict", "Users-2", "Approved.", true, "Platform.SoDMode must be single-operator or enforce, not 'strict'.", TestName = "{m}(unknown mode)")]
    [TestCase("uat-signoff-guard", "enforce", "Users-1", "", false, "Separation of duties holds for 'UAT sign-off' (Platform.SoDMode enforce): answered by alice.", TestName = "{m}(UAT sign-off by the creator)")]
    [Capability("CAP-OCT-004")]
    public void Should_SodGuard_DecisionTable_AppliesTheCreatorRuleOfTheMode(string step, string mode, string approverId, string notes, bool fails, string message)
    {
        using var runner = new OctopusScriptRunner();
        var prefix = step == "sod-guard" ? GoNoGo : UatSignOff;
        var approver = approverId == "Users-1" ? "alice" : "bob";
        var variables = Variables(mode, "false");
        variables[prefix + "ResponsibleUser.Id"] = approverId;
        variables[prefix + "ResponsibleUser.Username"] = approver;
        variables[prefix + "Notes"] = notes;

        var result = runner.Run(OctopusScriptRunner.ScriptBody(Process, step), variables);

        result.Failed.ShouldBe(fails, result.ToString());
        if (fails)
        {
            result.FailMessage.ShouldBe(message, result.ToString());
            result.Outputs.ShouldBeEmpty();
            return;
        }

        result.Log.ShouldContain(message, Case.Sensitive, result.ToString());
        result.Outputs["SodGuard.Result"].ShouldBe("passed");
        result.Outputs["SodGuard.Approver"].ShouldBe(approver);
        result.Outputs["SodGuard.Reason"].ShouldBe(notes.Split('\n')[0].Trim());
    }

    /// <summary>A missing approver, creator or automation user fails the guard closed.</summary>
    [TestCase(GoNoGo + "ResponsibleUser.Id", "No answer is recorded for 'Prod go/no-go'; failing closed.", TestName = "{m}(missing approver)")]
    [TestCase("Octopus.Deployment.CreatedBy.Id", "Cannot read the creator of this deployment; failing closed.", TestName = "{m}(missing creator)")]
    [TestCase("Platform.AutomationUsername", "Platform.AutomationUsername is empty (library variable set Platform Environment); failing closed.", TestName = "{m}(missing automation user)")]
    [Capability("CAP-OCT-004")]
    public void Should_SodGuard_MissingAnswerOrIdentity_FailsClosed(string missing, string message)
    {
        using var runner = new OctopusScriptRunner();
        var variables = Variables("single-operator", "false");
        variables[GoNoGo + "ResponsibleUser.Id"] = "Users-2";
        variables[GoNoGo + "ResponsibleUser.Username"] = "bob";
        variables[GoNoGo + "Notes"] = "Approved.";
        variables.Remove(missing);

        var result = runner.Run(OctopusScriptRunner.ScriptBody(Process, "sod-guard"), variables);

        result.FailMessage.ShouldBe(message, result.ToString());
        result.Outputs.ShouldBeEmpty();
    }

    /// <summary>
    /// The step template takes its inputs from the template parameters SodGuard.*: the creator rule applies when
    /// SodGuard.CheckCreator is True (enforce mode fails the creator) and not when it is False.
    /// </summary>
    [TestCase("True", true, TestName = "{m}(creator rule on)")]
    [TestCase("False", false, TestName = "{m}(creator rule off)")]
    [Capability("CAP-OCT-004")]
    public void Should_SodGuardTemplate_TemplateParameters_DriveTheGuard(string checkCreator, bool fails)
    {
        using var runner = new OctopusScriptRunner();
        var variables = Variables("enforce", "false");
        variables["SodGuard.ApprovalStep"] = "Prod go/no-go";
        variables["SodGuard.OtherSteps"] = "Hotfix justification";
        variables["SodGuard.CheckCreator"] = checkCreator;
        variables[GoNoGo + "ResponsibleUser.Id"] = "Users-1";
        variables[GoNoGo + "ResponsibleUser.Username"] = "alice";
        variables[GoNoGo + "Notes"] = "Approved.";

        var result = runner.Run(OctopusRepository.Read("octopus/step-templates/sod-guard.ps1"), variables);

        result.Failed.ShouldBe(fails, result.ToString());
        if (fails)
        {
            result.FailMessage.ShouldNotBeNull().ShouldStartWith("Separation of duties (enforce): alice created this deployment", Case.Sensitive);
        }
        else
        {
            result.Outputs["SodGuard.Approver"].ShouldBe("alice");
        }
    }

    /// <summary>
    /// The automation user may answer an intervention only in intervention test mode and only with the reason
    /// conformance:&lt;run-id&gt; or e2e:&lt;run-id&gt; in the first line of Notes: on the Prod go/no-go, and on the Hotfix
    /// justification of channel Hotfix; a person's hotfix answer and a justification that did not run pass.
    /// </summary>
    [TestCase("go/no-go", "false", "conformance:run-1", true, "Separation of duties: 'Prod go/no-go' was answered by the automation user aisf-service-account while Platform.InterventionTestMode is not true.", TestName = "{m}(go/no-go outside test mode)")]
    [TestCase("go/no-go", "True", "conformance:20260925.1", false, "'Prod go/no-go' was answered by the automation user in intervention test mode: conformance:20260925.1", TestName = "{m}(go/no-go with a conformance reason)")]
    [TestCase("go/no-go", "true", "e2e:P1-07\r\nmore notes", false, "'Prod go/no-go' was answered by the automation user in intervention test mode: e2e:P1-07", TestName = "{m}(go/no-go with an e2e reason)")]
    [TestCase("go/no-go", "true", "approved", true, "Separation of duties: the automation user answered 'Prod go/no-go' without the reason conformance:<run-id> or e2e:<run-id> (Notes: 'approved').", TestName = "{m}(go/no-go without a run reason)")]
    [TestCase("hotfix", "false", "INC-7", true, "Separation of duties: 'Hotfix justification' was answered by the automation user AISF-Service-Account while Platform.InterventionTestMode is not true.", TestName = "{m}(hotfix channel, automation outside test mode)")]
    [TestCase("hotfix", "true", "e2e:hotfix-1", false, "'Hotfix justification' was answered by the automation user in intervention test mode: e2e:hotfix-1", TestName = "{m}(hotfix channel, automation with a reason)")]
    [TestCase("hotfix-by-person", "false", "INC-7: payment outage", false, "Separation of duties holds for 'Prod go/no-go' (Platform.SoDMode single-operator): answered by bob.", TestName = "{m}(hotfix channel, person)")]
    [TestCase("default-channel", "false", "", false, "Separation of duties holds for 'Prod go/no-go' (Platform.SoDMode single-operator): answered by bob.", TestName = "{m}(no hotfix justification)")]
    [Capability("CAP-OCT-005")]
    public void Should_SodGuard_AutomationAnswers_OnlyInTestModeWithARunReason(string answered, string testMode, string notes, bool fails, string message)
    {
        using var runner = new OctopusScriptRunner();
        var automationAnswers = answered == "go/no-go";
        var variables = Variables("single-operator", testMode);
        variables[GoNoGo + "ResponsibleUser.Id"] = automationAnswers ? "Users-9" : "Users-2";
        variables[GoNoGo + "ResponsibleUser.Username"] = automationAnswers ? Automation.ToLowerInvariant() : "bob";
        variables[GoNoGo + "Notes"] = automationAnswers ? notes : "Approved.";
        if (answered is "hotfix" or "hotfix-by-person")
        {
            variables[Hotfix + "ResponsibleUser.Username"] = answered == "hotfix" ? Automation : "carol";
            variables[Hotfix + "Notes"] = notes;
        }

        var result = runner.Run(OctopusScriptRunner.ScriptBody(Process, "sod-guard"), variables);

        result.Failed.ShouldBe(fails, result.ToString());
        if (fails)
        {
            result.FailMessage.ShouldBe(message, result.ToString());
            return;
        }

        result.Log.ShouldContain(message, Case.Sensitive, result.ToString());
        result.Outputs["SodGuard.Result"].ShouldBe("passed");
    }

    private static Dictionary<string, string> Variables(string mode, string testMode) => new(StringComparer.Ordinal)
    {
        ["Platform.SoDMode"] = mode,
        ["Platform.InterventionTestMode"] = testMode,
        ["Platform.AutomationUsername"] = Automation,
        ["Octopus.Deployment.CreatedBy.Id"] = "Users-1",
    };
}
