# pymobiledevice3 Replacement — Implementation Plan

> **Goal:** Replace all 6 libimobiledevice tools with single pymobiledevice3.exe. Clean removal, no fallback.

**Architecture:** Single `_pymd3` field in IosService, ToolLauncher._toolsDir updated to `tools/pymobiledevice3`, all 11 IosService methods remapped to pymobiledevice3 subcommands.

**Tech Stack:** C# WPF .NET 8, pymobiledevice3 via PyInstaller

---

### Task 1: Bundle pymobiledevice3.exe

**Files:**
- Create: `tools/pymobiledevice3/pymobiledevice3.exe`

- [ ] **Step 1: Build the self-contained executable**

Run on machine with Python 3.10+:
```powershell
pip install pymobiledevice3 pyinstaller
pyinstaller --onefile --name pymobiledevice3 -m pymobiledevice3.cli --clean
```

- [ ] **Step 2: Place in tools directory**

```powershell
New-Item -ItemType Directory -Path "D:\OpenCode\QAQC\QADeviceTool\tools\pymobiledevice3" -Force
Copy-Item "dist\pymobiledevice3.exe" "D:\OpenCode\QAQC\QADeviceTool\tools\pymobiledevice3\pymobiledevice3.exe"
```

- [ ] **Step 3: Verify it works**

```powershell
D:\OpenCode\QAQC\QADeviceTool\tools\pymobiledevice3\pymobiledevice3.exe usbmux list
```
Expected: Lists connected iOS devices or "No connected devices."

---

### Task 2: Update ToolLauncher — working directory

**Files:**
- Modify: `src/QADeviceTool.App/Helpers/ToolLauncher.cs:20`

- [ ] **Step 1: Change _toolsDir**

```csharp
// OLD:
_toolsDir = Path.Combine(AppContext.BaseDirectory, "tools", "iMobileDevice");

// NEW:
_toolsDir = Path.Combine(AppContext.BaseDirectory, "tools", "pymobiledevice3");
```

- [ ] **Step 2: Verify the ToolsDirectory property still works**

The public `ToolsDirectory => _toolsDir` property is used by IosService. It should now return the pymobiledevice3 path.

- [ ] **Step 3: Build check**

```powershell
dotnet build -c Release
```
Expected: 0 errors.

---

### Task 3: Rewrite IosService.cs — constructor and availability check

**Files:**
- Modify: `src/QADeviceTool.App/Services/IosService.cs:1-30,33-75`

- [ ] **Step 1: Replace constructor**

Remove 6 tool fields, add single `_pymd3` field:

```csharp
public class IosService : IIosService
{
    private readonly string _pymd3;

    public IosService()
    {
        _pymd3 = Helpers.ToolResolver.Resolve("pymobiledevice3.exe");
    }
```

Remove: `_ideviceId`, `_ideviceInfo`, `_ideviceSyslog`, `_ideviceScreenshot`, `_ideviceInstaller`, `_afcClient` fields and their initializers.

- [ ] **Step 2: Rewrite CheckAvailabilityAsync**

```csharp
public async Task<ToolStatus> CheckAvailabilityAsync()
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3, "usbmux list", 10000).ConfigureAwait(false);
        return new ToolStatus
        {
            Name = "pymobiledevice3 (iOS Tools)",
            Description = "Required for iOS device communication",
            IsInstalled = result.Success,
            Version = "Installed",
            Path = ToolLauncher.ToolsDirectory,
            StatusMessage = result.Success ? "iOS tools ready" : "iOS tools not responding"
        };
    }
    catch (Exception ex)
    {
        AppLogger.Log.Error(ex, "[IosService] CheckAvailabilityAsync failed");
        return new ToolStatus { Name = "pymobiledevice3 (iOS Tools)", IsInstalled = false, StatusMessage = ex.Message };
    }
}
```

- [ ] **Step 3: Build check**
Expected: Errors on remaining methods (they still reference old tool fields). We fix them next.

---

### Task 4: Rewrite IosService.cs — device detection and info

**Files:**
- Modify: `src/QADeviceTool.App/Services/IosService.cs:77-153`

- [ ] **Step 1: Rewrite GetConnectedDevicesAsync**

```csharp
public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
{
    var devices = new List<DeviceInfo>();
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3, "usbmux list", 10000).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return devices;

        foreach (var line in result.Output.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // Parse UDID from "UDID  (Model)  Status" format
            var parts = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var udid = parts[0];
            var device = new DeviceInfo
            {
                Serial = udid, Id = udid,
                Platform = DevicePlatform.iOS,
                ConnectionState = DeviceConnectionState.Online
            };
            device = await GetDeviceDetailsAsync(device).ConfigureAwait(false);
            devices.Add(device);
        }
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] GetConnectedDevicesAsync failed"); }
    return devices;
}
```

- [ ] **Step 2: Rewrite GetDeviceDetailsAsync**

pymobiledevice3 `lockdown info` outputs key:value pairs. Keys differ from ideviceinfo.

```csharp
public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3, $"-u {device.Serial} lockdown info", 10000).ConfigureAwait(false);
        if (!result.Success) return device;

        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DeviceName:"))
                device.Name = trimmed["DeviceName:".Length..].Trim();
            else if (trimmed.StartsWith("ProductType:"))
                device.Model = trimmed["ProductType:".Length..].Trim();
            else if (trimmed.StartsWith("ProductVersion:"))
                device.OsVersion = trimmed["ProductVersion:".Length..].Trim();
            else if (trimmed.StartsWith("BatteryCurrentCapacity:"))
                device.BatteryLevel = trimmed["BatteryCurrentCapacity:".Length..].Trim() + "%";
        }
        if (string.IsNullOrEmpty(device.Model)) device.Model = "iOS Device";
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, $"[IosService] GetDeviceDetailsAsync failed for {device.Serial}"); }
    return device;
}
```

- [ ] **Step 3: Build check**

---

### Task 5: Rewrite IosService.cs — log capture and screenshots

**Files:**
- Modify: `src/QADeviceTool.App/Services/IosService.cs:156-184`

- [ ] **Step 1: Rewrite StartLogCapture**

pymobiledevice3 `syslog live` replaces `idevicesyslog`:

```csharp
public System.Diagnostics.Process? StartLogCapture(string udid, string outputFilePath)
{
    _ = outputFilePath; // kept for interface parity with AdbService
    try
    {
        return ToolLauncher.StartLongRunning(_pymd3, $"-u {udid} syslog live");
    }
    catch (Exception ex)
    {
        AppLogger.Log.Error(ex, "[IosService] StartLogCapture failed");
        return null;
    }
}
```

- [ ] **Step 2: Rewrite CaptureScreenshotAsync**

```csharp
public async Task<bool> CaptureScreenshotAsync(string udid, string outputPath)
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3, $"-u {udid} screenshot \"{outputPath}\"", 15000).ConfigureAwait(false);
        return result.Success;
    }
    catch (Exception ex)
    {
        AppLogger.Log.Error(ex, "[IosService] CaptureScreenshotAsync failed");
        return false;
    }
}
```

- [ ] **Step 3: Build check**

---

### Task 6: Rewrite IosService.cs — app management (install, list, uninstall)

**Files:**
- Modify: `src/QADeviceTool.App/Services/IosService.cs:186-280`

- [ ] **Step 1: Rewrite InstallIpaAsync**

No 2GB limit, no temp file copy needed. pymobiledevice3 handles paths directly:

```csharp
public async Task<(bool Success, string Message)> InstallIpaAsync(string udid, string ipaPath, Action<string>? outputCallback = null)
{
    try
    {
        outputCallback?.Invoke($"Installing: {ipaPath}");
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} apps install \"{ipaPath}\"", 600000, outputCallback).ConfigureAwait(false);

        if (result.Success)
            return (true, "IPA installed successfully.");

        string error = result.Error ?? result.Output ?? $"Exit code: {result.ExitCode}";
        return (false, $"Install failed: {error.Trim()}");
    }
    catch (Exception ex)
    {
        AppLogger.Log.Error(ex, "[IosService] InstallIpaAsync failed");
        return (false, ex.Message);
    }
}
```

- [ ] **Step 2: Rewrite ListInstalledAppsAsync**

pymobiledevice3 outputs app list as structured text. Parse bundle IDs:

```csharp
public async Task<List<AppItem>> ListInstalledAppsAsync(string udid)
{
    var apps = new List<AppItem>();
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} apps list --all", 15000).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Output)) return apps;

        foreach (var line in result.Output.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("Total")) continue;
            // Format: "bundle.id  version  name"
            var parts = trimmed.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var pkg = parts[0];
            if (!pkg.Contains('.')) continue; // Skip non-bundle-identifier lines
            var ver = parts.Length > 1 ? parts[1] : "";
            var name = parts.Length > 2 ? parts[2] : pkg;
            apps.Add(new AppItem { PackageId = pkg, Name = name, Version = ver, Platform = DevicePlatform.iOS });
        }
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] ListInstalledAppsAsync failed"); }
    return apps.OrderBy(a => a.Name).ToList();
}
```

- [ ] **Step 3: Rewrite UninstallAppAsync**

```csharp
public async Task<bool> UninstallAppAsync(string udid, string packageId)
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} apps uninstall {packageId}", 20000).ConfigureAwait(false);
        return result.Success;
    }
    catch (Exception ex)
    {
        AppLogger.Log.Error(ex, "[IosService] UninstallAppAsync failed");
        return false;
    }
}
```

- [ ] **Step 4: Build check**

---

### Task 7: Rewrite IosService.cs — file operations (list, pull, push, delete)

**Files:**
- Modify: `src/QADeviceTool.App/Services/IosService.cs:282-349`

- [ ] **Step 1: Rewrite ListDirectoryAsync**

pymobiledevice3 `afc ls` output: similar column format but different spacing:

```csharp
public async Task<List<DeviceFile>> ListDirectoryAsync(string udid, string path)
{
    var files = new List<DeviceFile>();
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} afc ls \"{path}\"", 15000).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return files;

        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;

            var mode = parts[0];
            long.TryParse(parts[4], out var size);
            var name = string.Join(" ", parts.Skip(5));
            DateTime modDate = DateTime.MinValue;
            if (parts.Length >= 8)
                DateTime.TryParse($"{parts[5]} {parts[6]} {parts[7]}", out modDate);

            files.Add(new DeviceFile
            {
                Name = name,
                Path = path == "/" ? $"/{name}" : $"{path}/{name}",
                IsDirectory = mode.StartsWith("d"),
                Size = size,
                ModifiedDate = modDate
            });
        }
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] ListDirectoryAsync failed"); }
    return files;
}
```

- [ ] **Step 2: Rewrite PullFileAsync, PushFileAsync, DeleteFileAsync**

```csharp
public async Task<bool> PullFileAsync(string udid, string remotePath, string localPath)
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} afc pull \"{remotePath}\" \"{localPath}\"", 30000).ConfigureAwait(false);
        return result.Success;
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PullFileAsync failed"); return false; }
}

public async Task<bool> PushFileAsync(string udid, string localPath, string remotePath)
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} afc push \"{localPath}\" \"{remotePath}\"", 30000).ConfigureAwait(false);
        return result.Success;
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PushFileAsync failed"); return false; }
}

public async Task<bool> DeleteFileAsync(string udid, string path)
{
    try
    {
        var result = await ToolLauncher.RunAsync(_pymd3,
            $"-u {udid} afc rm \"{path}\"", 10000).ConfigureAwait(false);
        return result.Success;
    }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] DeleteFileAsync failed"); return false; }
}
```

- [ ] **Step 3: Remove unused imports and helper methods**

Remove `using System.Text.RegularExpressions` at top if only used by removed methods.
Remove `ParseCsvLine` helper method (no longer needed).

- [ ] **Step 4: Build check**

---

### Task 8: Update DependencyChecker

**Files:**
- Modify: `src/QADeviceTool.App/Services/DependencyChecker.cs`

- [ ] **Step 1: Replace libimobiledevice check with pymobiledevice3**

Remove `CheckiTunes` method entirely (pymobiledevice3 doesn't need iTunes).
Update the iOS section of `CheckAllAsync`:

Old code that called `_iosService.CheckAvailabilityAsync()` remains — it's already been rewritten. But the DependencyChecker has its own CheckiTunes() and CheckAndroidDriver() methods. CheckiTunes() should be removed. CheckAllAsync should only call the IosService check.

```csharp
// In CheckAllAsync, remove direct Checks of iTunes and Android Driver.
// The IosService availability check (pymobiledevice3 usbmux list) is sufficient.
```

- [ ] **Step 2: Build check**

---

### Task 9: Update build pipeline files

**Files:**
- Modify: `build_installer.bat`
- Modify: `installer/setup.iss`
- Modify: `src/QADeviceTool.App/QADeviceTool.App.csproj`

- [ ] **Step 1: Update build_installer.bat**

Replace:
```batch
xcopy /E /I /Y "tools\iMobileDevice" "publish\app\tools\iMobileDevice"
```
With:
```batch
xcopy /E /I /Y "tools\pymobiledevice3" "publish\app\tools\pymobiledevice3"
```

- [ ] **Step 2: Update installer/setup.iss**

Replace all `Source: "app\tools\iMobileDevice\*"` entries with:
```
Source: "app\tools\pymobiledevice3\pymobiledevice3.exe"; DestDir: "{app}\tools\pymobiledevice3"
```

Remove all iMobileDevice DLL entries (hundreds of lines). The pymobiledevice3 .exe is a single self-contained file.

- [ ] **Step 3: Update csproj**

In the `<ItemGroup>` that copies tools, replace:
```xml
<Content Include="..\..\tools\iMobileDevice\**\*">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <Link>tools\iMobileDevice\%(RecursiveDir)%(Filename)%(Extension)</Link>
</Content>
```
With:
```xml
<Content Include="..\..\tools\pymobiledevice3\**\*">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <Link>tools\pymobiledevice3\%(RecursiveDir)%(Filename)%(Extension)</Link>
</Content>
```

- [ ] **Step 4: Build check**

---

### Task 10: Remove old iMobileDevice tools directory

**Files:**
- Delete: `src/QADeviceTool.App/tools/iMobileDevice/` (entire directory)

- [ ] **Step 1: Delete the directory**

```powershell
Remove-Item -Recurse -Force "D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\tools\iMobileDevice"
```

- [ ] **Step 2: Also remove from root tools/ if present**

```powershell
Remove-Item -Recurse -Force "D:\OpenCode\QAQC\QADeviceTool\tools\iMobileDevice" -ErrorAction SilentlyContinue
```

---

### Task 11: Final build, publish, verify

- [ ] **Step 1: Full build**

```powershell
cd D:\OpenCode\QAQC\QADeviceTool
dotnet build -c Release
```
Expected: 0 errors.

- [ ] **Step 2: Publish**

```powershell
dotnet publish src\QADeviceTool.App\QADeviceTool.App.csproj -c Release -r win-x64 --self-contained true -o publish\app
```

- [ ] **Step 3: Verify pymobiledevice3.exe is in publish output**

```powershell
Test-Path "D:\OpenCode\QAQC\QADeviceTool\publish\app\tools\pymobiledevice3\pymobiledevice3.exe"
```
Expected: True

- [ ] **Step 4: Verify old iMobileDevice is gone**

```powershell
Test-Path "D:\OpenCode\QAQC\QADeviceTool\publish\app\tools\iMobileDevice"
```
Expected: False

- [ ] **Step 5: Rebuild distributions**

```powershell
Remove-Item -Path "installer\app" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path "publish\app\*" -Destination "installer\app\" -Recurse -Force
& "C:\InnoSetup\ISCC.exe" "installer\setup.iss"
Compress-Archive -Path "publish\app\*" -DestinationPath "publish\LogPro_Portable_v2.8.0.zip" -Force
```

- [ ] **Step 6: Verify distribution size**

Should be ~210-220 MB (removed 40MB iMobileDevice, added ~60MB pymobiledevice3).

---

### Task 12: New Features Documentation

- [ ] **Step 1: Update README.md**

Note pymobiledevice3 as the iOS backend. Remove libimobiledevice references.

- [ ] **Step 2: Document new capabilities**

pymobiledevice3 unlocks these features that can be implemented in future tasks:
- iOS Shell access (`developer shell`)
- iOS Screen recording (`developer dvt --screenrecord`)
- iOS Crash log retrieval (`crash list/pull`)
- iOS Diagnostics (`diagnostics`)
- iOS Push notifications (`notification post`)
- iOS Developer tools (`developer dvt --proclist`)
- No iTunes/Apple Mobile Device Service dependency
