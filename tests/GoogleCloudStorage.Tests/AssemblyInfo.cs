using Xunit;

// The connector's StorageClient/UrlSigner caches and the EmulatorBaseUri test seam
// are process-wide. Run collections sequentially so the offline and integration suites
// never race on that shared state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
