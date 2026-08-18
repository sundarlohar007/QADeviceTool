// Heavy end-to-end tests (CLI child processes, 1M-line perf smoke) — serialized to avoid
// CPU/subprocess contention that flakes timing-sensitive assertions.
[CollectionDefinition("HeavyE2E", DisableParallelization = true)]
public class HeavyE2ECollection;
