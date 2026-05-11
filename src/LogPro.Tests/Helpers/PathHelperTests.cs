using LogPro.Helpers;

namespace LogPro.Tests.Helpers;

public class PathHelperTests
{
    [Fact]
    public void GetDefaultSessionsDirectory_ReturnsLocalAppDataPath()
    {
        var path = PathHelper.GetDefaultSessionsDirectory();
        
        path.Should().Contain("QAQCDeviceTool");
        path.Should().Contain("Sessions");
    }

    [Fact]
    public void GetConfigFilePath_ReturnsConfigFilePath()
    {
        var path = PathHelper.GetConfigFilePath();
        
        path.Should().Contain("QAQCDeviceTool");
        path.Should().Contain("config.txt");
    }

    [Fact]
    public void CreateSessionDirectory_CreatesDirectoryWithCorrectFormat()
    {
        using var tempDir = new TempDirectory();
        
        var sessionsDir = Path.Combine(tempDir.DirectoryPath, "Sessions");
        var result = PathHelper.CreateSessionDirectory("TestDevice", sessionsDir);
        
        result.Should().StartWith(sessionsDir);
        result.Should().Contain("TestDevice");
        Directory.Exists(result).Should().BeTrue();
    }

    [Fact]
    public void CreateSessionDirectory_ReplacesInvalidChars()
    {
        using var tempDir = new TempDirectory();
        
        var result = PathHelper.CreateSessionDirectory("Test|Device<>", tempDir.DirectoryPath);
        
        result.Should().NotContain("|");
        result.Should().NotContain("<");
        result.Should().NotContain(">");
    }

    [Fact]
    public void IsCommandInPath_WhenCommandExists_ReturnsTrue()
    {
        var result = PathHelper.IsCommandInPath("cmd");
        result.Should().BeTrue();
    }

    [Fact]
    public void IsCommandInPath_WhenCommandNotExists_ReturnsFalse()
    {
        var result = PathHelper.IsCommandInPath("ThisCommandDoesNotExist12345");
        result.Should().BeFalse();
    }

    [Fact]
    public void FindInPath_WhenCommandExists_ReturnsFullPath()
    {
        var result = PathHelper.FindInPath("cmd");
        result.Should().NotBeNull();
        result.Should().Contain("cmd.exe");
    }

    [Fact]
    public void FindInPath_WhenCommandNotExists_ReturnsNull()
    {
        var result = PathHelper.FindInPath("ThisCommandDoesNotExist12345");
        result.Should().BeNull();
    }
}