namespace ReStore.Core.src.storage;

/// <summary>
/// Thrown by every provider when a remote object does not exist (download of a missing path).
/// Derives from <see cref="FileNotFoundException"/> so existing callers that classify
/// "missing artifact" keep working.
/// </summary>
public class StorageNotFoundException : FileNotFoundException
{
    public StorageNotFoundException(string message) : base(message)
    {
    }

    public StorageNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The storage path that was not found, when the provider knows it (lets the CLI say which snapshot or file is missing).</summary>
    public string? RemotePath { get; init; }
}

/// <summary>
/// Thrown when a provider needs interactive authorization (for example Google OAuth consent)
/// that did not complete, so an unattended run cannot block forever.
/// </summary>
public class StorageAuthRequiredException : Exception
{
    public StorageAuthRequiredException(string message) : base(message)
    {
    }

    public StorageAuthRequiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
