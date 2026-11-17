namespace ReStore.E2E;

/// <summary>
/// Tests that interrupt or race a running backup need the backup to still be running when the signal arrives. On a very fast disk or a
/// loaded CI machine that can fail either way, so a scenario that reports "the backup was already over" is repeated in a fresh workspace
/// (a few times) instead of being counted as a pass or a failure. A scenario that really breaks an invariant still fails at once.
/// </summary>
internal static class TimingRetry
{
    public const int Attempts = 4;

    /// <summary><paramref name="scenario"/> returns false when it was too late to test anything (the backup had finished).</summary>
    public static void Run(Func<Workspace, bool> scenario, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var workspace = Workspace.Create(name);
            if (scenario(workspace))
            {
                return;
            }
        }

        Assert.Fail($"The backup finished before it could be interrupted in all {Attempts} attempts; make BigData larger (TestFiles.cs).");
    }
}
