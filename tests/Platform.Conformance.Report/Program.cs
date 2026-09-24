using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Report;

return ReportCommands.Run(args, Console.Out, Console.Error, ProcessEnvironmentVariables.Instance, Environment.CurrentDirectory, SystemClock.Instance);
