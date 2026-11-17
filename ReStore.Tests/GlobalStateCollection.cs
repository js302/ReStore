namespace ReStore.Tests;

/// <summary>
/// Tests that touch process-global state (Console redirection, environment variables, the shared
/// AppPaths root/config/log files) join this collection so they never run in parallel.
/// </summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public sealed class GlobalStateCollection;
