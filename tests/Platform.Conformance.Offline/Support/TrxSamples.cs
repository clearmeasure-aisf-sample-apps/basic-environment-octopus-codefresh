using System.Globalization;
using System.Xml.Linq;

namespace Platform.Conformance.Offline.Support;

/// <summary>One result to put in a generated TRX file.</summary>
internal sealed record TrxSample(string ClassName, string Name, string Outcome, double Seconds = 0.5, string? Message = null);

/// <summary>Builds TRX documents shaped like the ones the VSTest TRX logger writes for NUnit.</summary>
internal static class TrxSamples
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static string Create(DateTimeOffset start, TimeSpan wallClock, params TrxSample[] samples)
    {
        var results = new XElement(Ns + "Results");
        var definitions = new XElement(Ns + "TestDefinitions");
        var index = 0;
        foreach (var sample in samples)
        {
            index++;
            var testId = $"00000000-0000-0000-0000-{index:000000000000}";
            var result = new XElement(
                Ns + "UnitTestResult",
                new XAttribute("executionId", $"e{index}"),
                new XAttribute("testId", testId),
                new XAttribute("testName", sample.Name),
                new XAttribute("computerName", "stub"),
                new XAttribute("duration", TimeSpan.FromSeconds(sample.Seconds).ToString("c", CultureInfo.InvariantCulture)),
                new XAttribute("outcome", sample.Outcome));
            if (sample.Message is not null)
            {
                result.Add(new XElement(Ns + "Output", new XElement(Ns + "ErrorInfo", new XElement(Ns + "Message", sample.Message), new XElement(Ns + "StackTrace", "   at Sample.Method()"))));
            }

            results.Add(result);
            definitions.Add(new XElement(
                Ns + "UnitTest",
                new XAttribute("name", sample.Name),
                new XAttribute("storage", "/stub/Sample.dll"),
                new XAttribute("id", testId),
                new XElement(Ns + "Execution", new XAttribute("id", $"e{index}")),
                new XElement(Ns + "TestMethod", new XAttribute("codeBase", "/stub/Sample.dll"), new XAttribute("adapterTypeName", "executor://nunit3testexecutor/"), new XAttribute("className", sample.ClassName), new XAttribute("name", sample.Name))));
        }

        var document = new XDocument(new XElement(
            Ns + "TestRun",
            new XAttribute("id", "00000000-0000-0000-0000-000000000000"),
            new XAttribute("name", "stub run"),
            new XElement(Ns + "Times", new XAttribute("creation", Iso(start)), new XAttribute("start", Iso(start)), new XAttribute("finish", Iso(start + wallClock))),
            results,
            definitions));
        return document.ToString();
    }

    public static Stream AsStream(string trx) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(trx));

    private static string Iso(DateTimeOffset time) => time.ToString("o", CultureInfo.InvariantCulture);
}
