global using Xunit;

// Process-level fuzz and restart cases inspect/terminate worker children and must not overlap
// or race their leak accounting.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
