using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class PasswordProviderTests
{
    [Fact]
    public async Task StaticPasswordProvider_ShouldReturnConfiguredPassword()
    {
        var provider = new StaticPasswordProvider("Secret123");

        var password = await provider.GetPasswordAsync();

        password.Should().Be("Secret123");
        provider.IsPasswordSet().Should().BeTrue();
    }

    [Fact]
    public async Task StaticPasswordProvider_ShouldReportNotSet_WhenPasswordIsNull()
    {
        var provider = new StaticPasswordProvider(null);

        var password = await provider.GetPasswordAsync();

        password.Should().BeNull();
        provider.IsPasswordSet().Should().BeFalse();
    }

    [Fact]
    public async Task StaticPasswordProvider_ClearPassword_ShouldBeNoOp()
    {
        var provider = new StaticPasswordProvider("KeepMe");

        provider.ClearPassword();
        var passwordAfterClear = await provider.GetPasswordAsync();

        passwordAfterClear.Should().Be("KeepMe");
        provider.IsPasswordSet().Should().BeTrue();
    }
}
