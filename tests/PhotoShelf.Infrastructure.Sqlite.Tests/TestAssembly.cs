using Xunit;

// Fixtures clear the process-wide SQLite pools before removing their own databases.
// Do not let that cleanup overlap another fixture's open connections. Tests that
// explicitly exercise concurrent readers/writers still run those tasks together.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
