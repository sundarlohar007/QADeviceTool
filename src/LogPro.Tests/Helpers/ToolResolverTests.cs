using LogPro.Helpers;

namespace LogPro.Tests.Helpers;

public class ToolResolverTests
{
    [Fact]
    public void ToolsDirectory_ReturnsPathUnderAppBaseDirectory()
    {
        var toolsDir = ToolResolver.ToolsDirectory;
        
        toolsDir.Should().Contain("tools");
        toolsDir.Should().StartWith(AppContext.BaseDirectory);
    }

    [Fact]
    public void Resolve_WhenToolsDirectoryMissing_ReturnsToolName()
    {
        var result = ToolResolver.Resolve("nonexistent_tool");
        
        result.Should().Be("nonexistent_tool");
    }

    [Fact]
    public void IsBundled_WhenPathIsInToolsDirectory_ReturnsTrue()
    {
        var bundledPath = Path.Combine(ToolResolver.ToolsDirectory, "subfolder", "tool.exe");
        
        ToolResolver.IsBundled(bundledPath).Should().BeTrue();
    }

    [Fact]
    public void IsBundled_WhenPathIsNotInToolsDirectory_ReturnsFalse()
    {
        var systemPath = @"C:\Windows\System32\cmd.exe";
        
        ToolResolver.IsBundled(systemPath).Should().BeFalse();
    }
}