using Moq;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class EncryptionServiceTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly string _testDir;

    public EncryptionServiceTests()
    {
        _loggerMock = new Mock<ILogger>();
        _testDir = Path.Combine(Path.GetTempPath(), "ReStoreEncTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task EncryptAndDecrypt_ShouldReturnOriginalContent()
    {
        var service = new EncryptionService(_loggerMock.Object);
        var originalFile = Path.Combine(_testDir, "original.txt");
        var encryptedFile = Path.Combine(_testDir, "encrypted.bin");
        var decryptedFile = Path.Combine(_testDir, "decrypted.txt");
        var password = "StrongPassword123!";
        var content = "This is a secret message that needs encryption.";

        await File.WriteAllTextAsync(originalFile, content);

        var metadata = await service.EncryptFileAsync(originalFile, encryptedFile, password);
        await service.DecryptFileAsync(encryptedFile, decryptedFile, password, metadata);

        var decryptedContent = await File.ReadAllTextAsync(decryptedFile);
        decryptedContent.Should().Be(content);
    }

    [Fact]
    public async Task Decrypt_ShouldFail_WithWrongPassword()
    {
        var service = new EncryptionService(_loggerMock.Object);
        var originalFile = Path.Combine(_testDir, "original.txt");
        var encryptedFile = Path.Combine(_testDir, "encrypted.bin");
        var decryptedFile = Path.Combine(_testDir, "decrypted.txt");
        var password = "StrongPassword123!";
        var wrongPassword = "WrongPassword123!";

        await File.WriteAllTextAsync(originalFile, "content");
        var metadata = await service.EncryptFileAsync(originalFile, encryptedFile, password);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecryptFileAsync(encryptedFile, decryptedFile, wrongPassword, metadata));
    }

    [Fact]
    public void VerifyPassword_ShouldReturnTrue_ForCorrectPassword()
    {
        var service = new EncryptionService(_loggerMock.Object);
        var password = "StrongPassword123!";
        var salt = EncryptionService.GenerateSalt();

        var token = service.CreatePasswordVerificationToken(password, salt);
        var isValid = service.VerifyPassword(password, salt, token);

        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task EncryptFileAsync_ShouldPersistConfiguredKeyDerivationIterations()
    {
        var service = new EncryptionService(_loggerMock.Object);
        var originalFile = Path.Combine(_testDir, "iterated.txt");
        var encryptedFile = Path.Combine(_testDir, "iterated.bin");
        const int iterations = 250000;

        await File.WriteAllTextAsync(originalFile, "iteration sensitive content");

        var metadata = await service.EncryptFileAsync(
            originalFile,
            encryptedFile,
            "StrongPassword123!",
            EncryptionService.GenerateSalt(),
            iterations);

        metadata.KeyDerivationIterations.Should().Be(iterations);
    }

    [Fact]
    public void EncryptChunkDeterministic_ShouldProduceStableCiphertextForSameChunkAndKey()
    {
        var service = new EncryptionService(_loggerMock.Object);
        var masterKey = service.DeriveKeyFromPassword("StrongPassword123!", EncryptionService.GenerateSalt(), 1000);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("chunk payload");
        const string chunkId = "abc123chunkhash";

        var encryptedA = EncryptionService.EncryptChunkDeterministic(plaintext, masterKey, chunkId);
        var encryptedB = EncryptionService.EncryptChunkDeterministic(plaintext, masterKey, chunkId);

        encryptedA.Should().Equal(encryptedB);
    }

    [Fact]
    public void DecryptChunkDeterministic_ShouldFail_WhenPayloadIsTampered()
    {
        var service = new EncryptionService(_loggerMock.Object);
        var masterKey = service.DeriveKeyFromPassword("StrongPassword123!", EncryptionService.GenerateSalt(), 1000);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("chunk payload");
        const string chunkId = "abc123chunkhash";

        var encryptedPayload = EncryptionService.EncryptChunkDeterministic(plaintext, masterKey, chunkId);
        encryptedPayload[^1] ^= 0x01;

        var action = () => EncryptionService.DecryptChunkDeterministic(encryptedPayload, masterKey, chunkId);

        action.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}
