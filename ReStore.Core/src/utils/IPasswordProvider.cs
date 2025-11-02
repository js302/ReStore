namespace ReStore.Core.src.utils;

public interface IPasswordProvider
{
    Task<string?> GetPasswordAsync();
    bool IsPasswordSet();
    void ClearPassword();
}

public class StaticPasswordProvider : IPasswordProvider
{
    private readonly string? _password;

    public StaticPasswordProvider(string? password)
    {
        _password = password;
    }

    public Task<string?> GetPasswordAsync()
    {
        return Task.FromResult(_password);
    }

    public bool IsPasswordSet()
    {
        return !string.IsNullOrEmpty(_password);
    }

    public void ClearPassword()
    {
        // Static password provider doesn't support clearing
    }
}
