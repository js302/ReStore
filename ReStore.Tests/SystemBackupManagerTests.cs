using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class SystemBackupManagerTests
{
    [Theory]
    [InlineData("programs", "system_backups/programs/programs_backup_test.zip", "program-storage")]
    [InlineData("system_programs", "system_backups/programs/programs_backup_test.zip", "program-storage")]
    [InlineData("environment", "system_backups/environment/env_backup_test.zip", "environment-storage")]
    [InlineData("system_environment", "system_backups/environment/env_backup_test.zip", "environment-storage")]
    [InlineData("settings", "system_backups/settings/settings_backup_test.zip", "settings-storage")]
    [InlineData("system_settings", "system_backups/settings/settings_backup_test.zip", "settings-storage")]
    [InlineData("all", "system_backups/programs/programs_backup_test.zip", "program-storage")]
    [InlineData("all", "system_backups/environment/env_backup_test.zip", "environment-storage")]
    [InlineData("all", "system_backups/settings/settings_backup_test.zip", "settings-storage")]
    public async Task RestoreSystemAsync_ShouldSelectExpectedStorage_ForNormalizedBackupTypes(
        string backupType,
        string backupPath,
        string expectedStorageType)
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        var systemStateMock = new Mock<SystemState>(loggerMock.Object);
        var storageMock = new Mock<IStorage>();

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            StorageType = "default-system-storage",
            ProgramsStorageType = "program-storage",
            EnvironmentStorageType = "environment-storage",
            SettingsStorageType = "settings-storage"
        });

        configMock
            .Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(storageMock.Object);

        storageMock
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new FileNotFoundException("Stop after storage selection"));

        var manager = new SystemBackupManager(
            loggerMock.Object,
            configMock.Object,
            systemStateMock.Object);

        // Act
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            manager.RestoreSystemAsync(backupType, backupPath));

        // Assert
        configMock.Verify(c => c.CreateStorageAsync(expectedStorageType), Times.Once);
    }

    [Fact]
    public async Task RestoreSystemAsync_ShouldThrowArgumentException_WhenAllCannotInferComponentFromPath()
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        var systemStateMock = new Mock<SystemState>(loggerMock.Object);
        var configMock = new Mock<IConfigManager>();

        configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            StorageType = "default-system-storage",
            ProgramsStorageType = "program-storage",
            EnvironmentStorageType = "environment-storage",
            SettingsStorageType = "settings-storage"
        });

        var manager = new SystemBackupManager(
            loggerMock.Object,
            configMock.Object,
            systemStateMock.Object);

        // Act
        var action = () => manager.RestoreSystemAsync("all", "system_backups/unknown/type_backup_test.zip");

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(action);
        configMock.Verify(c => c.CreateStorageAsync(It.IsAny<string>()), Times.Never);
    }
}
