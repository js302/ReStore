using Moq;
using Xunit;
using FluentAssertions;
using ReStore.Core.src.utils;
using System.IO;
using System.Threading.Tasks;
using System;

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
        // Arrange
        var service = new EncryptionService(_loggerMock.Object);
        var originalFile = Path.Combine(_testDir, "original.txt");
        var encryptedFile = Path.Combine(_testDir, "encrypted.bin");
        var decryptedFile = Path.Combine(_testDir, "decrypted.txt");
        var password = "StrongPassword123!";
        var content = "This is a secret message that needs encryption.";

        await File.WriteAllTextAsync(originalFile, content);

        // Act
        var metadata = await service.EncryptFileAsync(originalFile, encryptedFile, password);
        await service.DecryptFileAsync(encryptedFile, decryptedFile, password, metadata);

        // Assert
        var decryptedContent = await File.ReadAllTextAsync(decryptedFile);
        decryptedContent.Should().Be(content);
    }

    [Fact]
    public async Task Decrypt_ShouldFail_WithWrongPassword()
    {
        // Arrange
        var service = new EncryptionService(_loggerMock.Object);
        var originalFile = Path.Combine(_testDir, "original.txt");
        var encryptedFile = Path.Combine(_testDir, "encrypted.bin");
        var decryptedFile = Path.Combine(_testDir, "decrypted.txt");
        var password = "StrongPassword123!";
        var wrongPassword = "WrongPassword123!";

        await File.WriteAllTextAsync(originalFile, "content");
        var metadata = await service.EncryptFileAsync(originalFile, encryptedFile, password);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            service.DecryptFileAsync(encryptedFile, decryptedFile, wrongPassword, metadata));
    }

    [Fact]
    public void VerifyPassword_ShouldReturnTrue_ForCorrectPassword()
    {
        // Arrange
        var service = new EncryptionService(_loggerMock.Object);
        var password = "StrongPassword123!";
        var salt = EncryptionService.GenerateSalt();
        
        // Act
        var token = service.CreatePasswordVerificationToken(password, salt);
        var isValid = service.VerifyPassword(password, salt, token);

        // Assert
        isValid.Should().BeTrue();
    }
}
