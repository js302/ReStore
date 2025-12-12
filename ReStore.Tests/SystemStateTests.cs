using Moq;
using Xunit;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System;

namespace ReStore.Tests;

public class SystemStateTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly string _testDir;
    private readonly string _stateFile;

    public SystemStateTests()
    {
        _loggerMock = new Mock<ILogger>();
        _testDir = Path.Combine(Path.GetTempPath(), "ReStoreStateTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
        _stateFile = Path.Combine(_testDir, "state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task AddOrUpdateFileMetadataAsync_ShouldCalculateHash_WhenFileExists()
    {
        // Arrange
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);
        
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "content");

        // Act
        await state.AddOrUpdateFileMetadataAsync(filePath);

        // Assert
        state.FileMetadata.Should().ContainKey(filePath);
        state.FileMetadata[filePath].Hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetChangedFiles_ShouldReturnFile_WhenHashChanges()
    {
        // Arrange
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);
        
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "content1");
        
        // Initial state
        await state.AddOrUpdateFileMetadataAsync(filePath);
        
        // Modify file
        await File.WriteAllTextAsync(filePath, "content2");

        // Act
        var changedFiles = state.GetChangedFiles(new List<string> { filePath }, BackupType.Incremental);

        // Assert
        changedFiles.Should().Contain(filePath);
    }

    [Fact]
    public async Task SaveAndLoadState_ShouldPersistData()
    {
        // Arrange
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);
        
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "content");
        await state.AddOrUpdateFileMetadataAsync(filePath);
        state.AddBackup(_testDir, "remote/path", false);

        // Act
        await state.SaveStateAsync();
        
        var newState = new SystemState(_loggerMock.Object);
        newState.SetStateFilePath(_stateFile);
        await newState.LoadStateAsync();

        // Assert
        newState.FileMetadata.Should().ContainKey(filePath);
        newState.BackupHistory.Should().ContainKey(_testDir);
        newState.FileMetadata[filePath].Hash.Should().Be(state.FileMetadata[filePath].Hash);
    }
}
