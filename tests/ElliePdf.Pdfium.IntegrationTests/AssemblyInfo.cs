using Xunit;

// PDFium has process-global initialization state. Production owns one engine
// lane per worker; integration tests create short-lived lanes to exercise that
// lifecycle. Running separate test classes concurrently would manufacture a
// topology the product explicitly forbids and can race PDFium's global state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
