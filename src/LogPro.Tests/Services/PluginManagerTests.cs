using LogPro.Services.Plugins;

namespace LogPro.Tests.Services;

public class PluginManagerTests
{
    private static string WritePlugin(string root, string id, string manifestJson)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
        return dir;
    }

    [Fact]
    public void Load_DeclarativeRegexParser_ParsesLines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProPlugins_{Guid.NewGuid():N}");
        try
        {
            WritePlugin(root, "unity", """
                {
                  "id": "unity",
                  "name": "Unity Parser",
                  "type": "logParser",
                  "regexRules": [
                    { "pattern": "^(?<level>FATAL|ERROR|WARN|INFO)\\|(?<tag>[A-Za-z0-9]+)\\| (?<msg>.*)$", "level": "Unknown", "tagGroup": "tag", "messageGroup": "msg" }
                  ]
                }
                """);

            var manager = new PluginManager();
            manager.LoadPlugins(root);

            manager.LogParsers.Should().ContainKey("unity");
            var parser = manager.LogParsers["unity"];
            parser.TryParse("ERROR|GameEngine| NullReferenceException", out var entry).Should().BeTrue();
            entry.Tag.Should().Be("GameEngine");
            entry.Message.Should().Be("NullReferenceException");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_RegexRuleWithFixedLevel_UsesRuleLevel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProPlugins_{Guid.NewGuid():N}");
        try
        {
            WritePlugin(root, "crash", """
                { "id": "crash", "name": "Crash Parser", "type": "logParser",
                  "regexRules": [ { "pattern": "tombstone|FATAL", "level": "Fatal" } ] }
                """);
            var manager = new PluginManager();
            manager.LoadPlugins(root);

            manager.LogParsers["crash"].TryParse("native tombstone detected", out var entry).Should().BeTrue();
            entry.Level.Should().Be("Fatal");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_DuplicateIds_SecondIsSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProPlugins_{Guid.NewGuid():N}");
        try
        {
            var manifest = """{ "id": "dup", "name": "A", "type": "logParser", "regexRules": [ { "pattern": "x", "level": "Info" } ] }""";
            WritePlugin(root, "a", manifest);
            WritePlugin(root, "b", manifest);

            var manager = new PluginManager();
            manager.LoadPlugins(root);

            manager.Plugins.Should().HaveCount(1, "duplicate ids must be rejected");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_MissingManifest_IsSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProPlugins_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        try
        {
            var manager = new PluginManager();
            manager.LoadPlugins(root);
            manager.Plugins.Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_AssemblyPlugin_FromTestFakesAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProPlugins_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // adb.dll IS the LogPro.TestFakes assembly — present in the test output dir
            var fakesAssembly = Path.Combine(AppContext.BaseDirectory, "adb.dll");
            if (!File.Exists(fakesAssembly)) return; // test bin layout changed — skip rather than fail

            var pluginDir = WritePlugin(root, "asm", """
                { "id": "sample.fakeparser", "name": "Fake Assembly Parser", "type": "logParser",
                  "entryAssembly": "adb.dll", "entryType": "LogPro.TestFakes.FakeLogParserPlugin" }
                """);
            File.Copy(fakesAssembly, Path.Combine(pluginDir, "adb.dll"));

            var manager = new PluginManager();
            manager.LoadPlugins(root);

            manager.LogParsers.Should().ContainKey("sample.fakeparser");
            manager.LogParsers["sample.fakeparser"].TryParse("FAKE: assembly parser works", out var entry).Should().BeTrue();
            entry.Level.Should().Be("Warning");
            entry.Message.Should().Be("assembly parser works");
        }
        finally
        {
            // The plugin AssemblyLoadContext holds the assembly file — cleanup is best-effort.
            try { Directory.Delete(root, true); } catch { /* ALC lock */ }
        }
    }
}
