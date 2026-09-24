using Platform.Onboarding.Cli;

namespace Platform.Onboarding;

/// <summary>Entry point of the onboarding kit; see <see cref="OnboardingCli"/>.</summary>
internal static class Program
{
    private static int Main(string[] args) =>
        new OnboardingCli(Console.Out, Console.Error, ProcessEnvironment.Instance, Directory.GetCurrentDirectory()).Run(args);
}
