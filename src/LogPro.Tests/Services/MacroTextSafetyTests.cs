using LogPro.Helpers;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class MacroTextSafetyTests
{
    [Theory]
    [InlineData("hello world", "hello%sworld")]
    [InlineData("O'Reilly", "O\\'Reilly")]
    [InlineData("plain", "plain")]
    public void SafeInputText_Sanitizes(string input, string expected)
    {
        MacroService.SafeInputText(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("x; rm -rf /")]
    [InlineData("$(reboot)")]
    [InlineData("a`id`b")]
    [InlineData("multi\nline")]
    public void SafeInputText_RejectsInjection(string input)
    {
        MacroService.SafeInputText(input).Should().BeEmpty();
    }

    [Fact]
    public void SafeInputText_NullOrWhitespace_Empty()
    {
        MacroService.SafeInputText(null).Should().BeEmpty();
        MacroService.SafeInputText("   ").Should().BeEmpty();
    }
}

public class PathHelperAclTests
{
    [Fact]
    public void RestrictDirectoryAccess_StillAllowsOwnerReadWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"LogProAclTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            PathHelper.RestrictDirectoryAccess(dir);

            var file = Path.Combine(dir, "probe.txt");
            var action = () => { File.WriteAllText(file, "ok"); return File.ReadAllText(file); };
            action.Should().NotThrow("owner must retain access after restriction");
            action().Should().Be("ok");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
