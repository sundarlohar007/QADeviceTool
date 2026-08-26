using LogPro.Services;

namespace LogPro.Tests;

/// <summary>
/// Isolates PreferencesService to a temp directory for the lifetime of the fixture —
/// prevents tests from reading/writing the user's real settings.json.
/// </summary>
public sealed class IsolatedPreferences : IDisposable
{
    private readonly IPreferencesStore _previous;
    private readonly string _dir;

    public IsolatedPreferences()
    {
        _previous = PreferencesService.Instance;
        _dir = Path.Combine(Path.GetTempPath(), $"LogProPrefs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        PreferencesService.Instance = new PreferencesStore(_dir);
    }

    public string DirectoryPath => _dir;

    public void Dispose()
    {
        PreferencesService.Instance = _previous;
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
