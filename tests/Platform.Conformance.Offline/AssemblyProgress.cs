using Platform.Conformance.Harness;

// Progress of the offline suite: an end line at each 10 % step and for each failure, no start lines (hundreds of quick
// tests); docs/runbooks/conformance.md, "Reading progress".
[assembly: ConformanceProgress(Label = "offline", EveryPercent = 10)]
