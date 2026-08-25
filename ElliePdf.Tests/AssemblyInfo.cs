using Xunit;

// PDFium is initialised once per process and the test harness shares a write buffer, so the suite
// runs serially rather than across parallel collections.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
