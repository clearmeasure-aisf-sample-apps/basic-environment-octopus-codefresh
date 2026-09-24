using System.Text.Json.Nodes;
using Platform.Onboarding.Descriptors;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class YamlJsonTests
{
    [Test]
    public void Should_Parse_PlainScalars_UseCoreSchemaTypes()
    {
        var document = YamlJson.Parse("a: 1\nb: true\nc: 1.5\nd: ~\ne: text\nf: '1'\n");

        var root = document.Root.ShouldBeOfType<JsonObject>();
        root["a"]!.GetValue<long>().ShouldBe(1);
        root["b"]!.GetValue<bool>().ShouldBeTrue();
        root["c"]!.GetValue<double>().ShouldBe(1.5);
        root["d"].ShouldBeNull();
        root["e"]!.GetValue<string>().ShouldBe("text");
        root["f"]!.GetValue<string>().ShouldBe("1");
    }

    [Test]
    public void Should_Parse_DuplicateKey_ReportsErrorWithLine()
    {
        var document = YamlJson.Parse("name: a\nname: b\n");

        document.Error.ShouldNotBeNull();
        document.Error.ShouldContain("Duplicate key");
        document.ErrorLine.ShouldBe(2);
    }

    [Test]
    public void Should_Parse_TwoDocuments_ReportsError()
    {
        var document = YamlJson.Parse("a: 1\n---\nb: 2\n");

        document.Error.ShouldNotBeNull();
        document.Error.ShouldContain("2 YAML documents");
    }

    [Test]
    public void Should_LineOf_NestedPointer_ReturnsSourceLine()
    {
        var document = YamlJson.Parse("top:\n  list:\n    - x\n    - y\n");

        document.LineOf("/top/list/1").ShouldBe(4);
        document.LineOf("/top/list/9").ShouldBe(3);
    }
}
