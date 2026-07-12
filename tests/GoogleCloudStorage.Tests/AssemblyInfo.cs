using Xunit;

// The connector's StorageClient/UrlSigner caches and the GCSCONNECTOR_EMULATOR_HOST environment
// variable are process-wide. Run collections sequentially so the offline and integration suites
// never race on that shared state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
