// The shared sleep and wake cycles of both tiers and their fixtures run in NUnit's parallel shift; every test of them
// blocks a worker while it waits for its phase, so the pool must hold all of them at once for both tiers to start together.
[assembly: LevelOfParallelism(16)]
