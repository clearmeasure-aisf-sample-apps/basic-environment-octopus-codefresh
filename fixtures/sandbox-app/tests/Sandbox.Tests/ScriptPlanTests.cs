using Sandbox.Migrator;

namespace Sandbox.Tests;

[TestFixture]
public sealed class ScriptPlanTests
{
    [Test]
    public void Should_Pending_UnappliedScripts_ReturnsThemInOrdinalOrder()
    {
        var pending = ScriptPlan.Pending(["0002_b.sql", "0001_a.sql", "0010_c.sql"], []);

        pending.ShouldBe(["0001_a.sql", "0002_b.sql", "0010_c.sql"]);
    }

    [Test]
    public void Should_Pending_JournaledScript_IsSkipped()
    {
        var pending = ScriptPlan.Pending(["0001_a.sql", "0002_b.sql"], ["0001_A.SQL"]);

        pending.ShouldBe(["0002_b.sql"]);
    }

    [Test]
    public void Should_Pending_FileThatIsNotSql_IsIgnored()
    {
        var pending = ScriptPlan.Pending(["README.md", "0001_a.sql", "0002_b.sql.disabled"], []);

        pending.ShouldBe(["0001_a.sql"]);
    }

    [Test]
    public void Should_Batches_GoSeparatedScript_SplitsAtGoLines()
    {
        var batches = ScriptPlan.Batches("CREATE TABLE t (id int);\nGO\nINSERT INTO t VALUES (1);\n  go  \n");

        batches.ShouldBe(["CREATE TABLE t (id int);", "INSERT INTO t VALUES (1);"]);
    }

    [Test]
    public void Should_Batches_ScriptWithoutGo_ReturnsOneBatch()
    {
        var batches = ScriptPlan.Batches("SELECT 1; -- a GOAL is not a separator");

        batches.Count.ShouldBe(1);
    }
}
