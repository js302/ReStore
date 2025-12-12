using Moq;
using Xunit;
using FluentAssertions;
using ReStore.Core.src.utils;
using System.Collections.Generic;
using System.IO;
using System;

namespace ReStore.Tests;

public class FileSelectionServiceTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<IConfigManager> _configMock;
    private readonly string _testDir;

    public FileSelectionServiceTests()
    {
        _loggerMock = new Mock<ILogger>();
        _configMock = new Mock<IConfigManager>();
        _testDir = Path.Combine(Path.GetTempPath(), "ReStoreSelectionTests_" + Guid.NewGuid());
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
    public void ShouldExcludeFile_WhenPatternMatches()
    {
        // Arrange
        _configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string> { "*.tmp", "*.log" });
        _configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        _configMock.Setup(c => c.MaxFileSizeMB).Returns(100);

        var service = new FileSelectionService(_loggerMock.Object, _configMock.Object);
        var file = Path.Combine(_testDir, "test.tmp");
        File.WriteAllText(file, "content");

        // Act
        var result = service.ShouldExcludeFile(file);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldExcludeFile_WhenPathMatches()
    {
        // Arrange
        var excludePath = Path.Combine(_testDir, "Temp");
        Directory.CreateDirectory(excludePath);
        
        _configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string>());
        _configMock.Setup(c => c.ExcludedPaths).Returns(new List<string> { excludePath });
        _configMock.Setup(c => c.MaxFileSizeMB).Returns(100);

        var service = new FileSelectionService(_loggerMock.Object, _configMock.Object);
        var file = Path.Combine(excludePath, "test.txt");
        File.WriteAllText(file, "content");

        // Act
        var result = service.ShouldExcludeFile(file);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldIncludeFile_WhenNoRulesMatch()
    {
        // Arrange
        _configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string> { "*.tmp" });
        _configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        _configMock.Setup(c => c.MaxFileSizeMB).Returns(100);

        var service = new FileSelectionService(_loggerMock.Object, _configMock.Object);
        var file = Path.Combine(_testDir, "important.docx");
        File.WriteAllText(file, "content");

        // Act
        var result = service.ShouldExcludeFile(file);

        // Assert
        result.Should().BeFalse();
    }
}
