namespace Finmind.Finance.Tests;

// Tests that create QUEUED runs share the database-wide claim queue (ADR-0009: claim takes the oldest queued run).
// Running them in one collection stops one test from claiming another test's run.
[CollectionDefinition(Name)]
public sealed class QueuedRunTests
{
    public const string Name = "Shared queued-run claim queue";
}
