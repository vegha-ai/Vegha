using Xunit;

// EnvironmentSecretSplitterTests mutates process-global environment variables
// (VEGHA_SECRET_*) to exercise EnvironmentSecretSplitter's CI-override path.
// The process environment is shared mutable state, so xUnit's default
// per-class parallelism lets one test's VEGHA_SECRET_API_KEY leak into another
// test's secret-load path (e.g. CollectionStoreSecretsTests reading
// "from-ci-override" instead of the stored value). Running this small
// assembly's tests serially keeps that global state isolated per test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
