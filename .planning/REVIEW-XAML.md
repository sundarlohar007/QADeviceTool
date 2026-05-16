---
phase: code-review-xaml-views
reviewed: 2026-05-07T00:00:00Z
depth: deep
files_reviewed: 20
files_reviewed_list:
  - src/QADeviceTool.App/MainWindow.xaml
  - src/QADeviceTool.App/MainWindow.xaml.cs
  - src/QADeviceTool.App/Views/SessionView.xaml
  - src/QADeviceTool.App/Views/SessionView.xaml.cs
  - src/QADeviceTool.App/Views/DeviceView.xaml
  - src/QADeviceTool.App/Views/DeviceView.xaml.cs
  - src/QADeviceTool.App/Views/DashboardView.xaml
  - src/QADeviceTool.App/Views/DashboardView.xaml.cs
  - src/QADeviceTool.App/Views/AppManagementView.xaml
  - src/QADeviceTool.App/Views/AppManagementView.xaml.cs
  - src/QADeviceTool.App/Views/FileExplorerView.xaml
  - src/QADeviceTool.App/Views/FileExplorerView.xaml.cs
  - src/QADeviceTool.App/Views/ShellView.xaml
  - src/QADeviceTool.App/Views/ShellView.xaml.cs
  - src/QADeviceTool.App/Views/VitalsView.xaml
  - src/QADeviceTool.App/Views/VitalsView.xaml.cs
  - src/QADeviceTool.App/Views/DeepLinkView.xaml
  - src/QADeviceTool.App/Views/DeepLinkView.xaml.cs
  - src/QADeviceTool.App/Views/SettingsView.xaml
  - src/QADeviceTool.App/Views/SettingsView.xaml.cs
  - src/QADeviceTool.App/Views/MacroView.xaml
  - src/QADeviceTool.App/Views/MacroView.xaml.cs
  - src/QADeviceTool.App/Views/StressTestView.xaml
  - src/QADeviceTool.App/Views/StressTestView.xaml.cs
  - src/QADeviceTool.App/Themes/DarkTheme.xaml
  - src/QADeviceTool.App/App.xaml
  - src/QADeviceTool.App/App.xaml.cs
  - src/QADeviceTool.App/Views/CommandPaletteWindow.xaml
  - src/QADeviceTool.App/Views/CommandPaletteWindow.xaml.cs
  - src/QADeviceTool.App/Converters/DeviceViewConverters.cs
  - src/QADeviceTool.App/Converters/LogLevelColorMultiConverter.cs
  - src/QADeviceTool.App/FeatureFlags.cs
  - src/QADeviceTool.App/ViewModels/MainViewModel.cs
findings:
  critical: 6
  warning: 10
  info: 9
  total: 25
status: issues_found
---

# Phase: XAML Views & Code-Behind Code Review Report

**Reviewed:** 2026-05-07
**Depth:** deep (cross-file binding trace, theme resource audit, XAML parse validation, event handler wiring verification)
**Files Reviewed:** 33 (20 primary + 13 cross-referenced)
**Status:** issues_found -- 6 BLOCKERs, 10 WARNINGs, 9 INFO items

## Summary

Deep review of the LogPro v2.8.0 XAML view layer: MainWindow, 11 UserControl views, theme resources, App.xaml, and supporting converters/ViewModels. Analysis covered binding path correctness (traced every `{Binding}` from XAML to ViewModel property), resource resolution (audited every `{StaticResource}` key against the theme dictionary), XAML parse validity (checked properties against target types), event handler wiring (verified code-behind method signatures), and memory safety of event subscriptions.

**Key concerns:**
1. **Navigate crash for Device view** -- `CommandParameter="Device"` mismatches the `Navigate()` switch case `"devices"`, causing the Device button to navigate to Dashboard instead. A one-character pluralization error.
2. **XamlParseException crash in FileExplorerView** -- `EnableRowVirtualization="True"` is set on a `DataGrid`, but this property does not exist on DataGrid (it is `VirtualizingStackPanel`-specific). The XAML parser will throw.
3. **CommandPaletteWindow permanently invisible** -- The root Border has fixed `Opacity="0"` but the FadeIn/FadeOut animations target the Window's Opacity, not the Border's. Combined opacity is always 0.
4. **Theme gap: CheckBox no implicit style** -- CheckBox has no implicit dark theme, so un-styled CheckBoxes render with system-default (black) foreground on dark backgrounds.
5. **Shell/FileExplorer cross-DataContext binding fragility** -- `RelativeSource AncestorType=Window` bindings assume a specific parent layout that breaks under DataTemplate virtualization.

---

## Critical Issues

### CR-01: Navigation Mismatch -- Device Button Navigates to Dashboard

**File:** `src/QADeviceTool.App/MainWindow.xaml:165` and `src/QADeviceTool.App/ViewModels/MainViewModel.cs:153`

**Issue:** The Device navigation button sends `CommandParameter="Device"`. The `Navigate()` method applies `ToLowerInvariant()`, producing `"device"`. The switch statement expects `"devices"` (plural). Since no case matches, it falls through to `_ => DashboardVM`, silently sending the user to the Dashboard instead of the Device view.

All other command parameters match their switch cases correctly (the pluralization discrepancy only affects "Device"/"devices").

**Fix:** Align the switch case with the XAML parameter or vice versa:
```csharp
// Option A: Fix the switch case (recommended -- aligns with the XAML)
"device" => DeviceVM,

// Option B: Fix the XAML command parameter
CommandParameter="Devices"
```

---

### CR-02: XamlParseException -- EnableRowVirtualization on DataGrid

**File:** `src/QADeviceTool.App/Views/FileExplorerView.xaml:82`

**Issue:** `EnableRowVirtualization="True"` is set as an attribute on `DataGrid`. This property belongs to `VirtualizingStackPanel` and is NOT a dependency property or attached property on `DataGrid` or its base classes. The WPF XAML parser will throw a `XamlParseException` at runtime when this view is loaded.

**Fix:** Remove the invalid attribute. DataGrid already has `VirtualizingPanel.IsVirtualizing="True"` (line 83) which enables virtualization correctly. If row-level virtualization is desired, set it on the items panel explicitly:
```xml
<DataGrid ...>
    <DataGrid.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel EnableRowVirtualization="True" />
        </ItemsPanelTemplate>
    </DataGrid.ItemsPanel>
</DataGrid>
```
Or simply remove line 82 entirely since DataGrid's default row virtualization is adequate with `VirtualizingPanel.IsVirtualizing="True"`.

---

### CR-03: CommandPaletteWindow Permanently Invisible

**File:** `src/QADeviceTool.App/Views/CommandPaletteWindow.xaml:29` and `src/QADeviceTool.App/Views/CommandPaletteWindow.xaml.cs:56-57,88-94`

**Issue:** The root `Border` has `Opacity="0"` (line 29). The `FadeIn` and `FadeOut` storyboards animate `Storyboard.TargetProperty="Opacity"` without a `TargetName`, so they target the Window's Opacity (the element on which `BeginStoryboard` is called). However, the combined visual opacity is `Window.Opacity * Border.Opacity = animated_window_opacity * 0 = 0`. The Border is always fully transparent regardless of the animation state. The Command Palette window renders but is completely invisible to the user.

Additionally, `fadeOut.Completed += ...` (line 89) adds a new handler on every call to `CloseWindow()`. If `OnDeactivated` fires multiple times during teardown, the handler accumulates, causing `Close()` and `WindowClosed?.Invoke()` to execute multiple times.

**Fix:** Animate the Border's Opacity, not the Window's, and remove the initial `Opacity="0"` or animate from that state:
```xml
<!-- In CommandPaletteWindow.xaml -->
<Storyboard x:Key="FadeIn">
    <DoubleAnimation Storyboard.TargetName="RootBorder"
                     Storyboard.TargetProperty="Opacity"
                     From="0" To="1" Duration="0:0:0.15"/>
</Storyboard>
<Storyboard x:Key="FadeOut">
    <DoubleAnimation Storyboard.TargetName="RootBorder"
                     Storyboard.TargetProperty="Opacity"
                     From="1" To="0" Duration="0:0:0.1"/>
</Storyboard>

<Border x:Name="RootBorder" Opacity="0" ...>
```
And in code-behind, guard against re-entry:
```csharp
private bool _isClosing;
private void CloseWindow()
{
    if (_isClosing) return;
    _isClosing = true;
    var fadeOut = (Storyboard)Resources["FadeOut"];
    fadeOut.Completed += (s, e) =>
    {
        WindowClosed?.Invoke();
        Close();
    };
    BeginStoryboard(fadeOut);
}
```

---

### CR-04: CheckBox Renders Invisible Text on Dark Background

**File:** `src/QADeviceTool.App/Themes/DarkTheme.xaml` (entire file) and `src/QADeviceTool.App/Views/SessionView.xaml:54`

**Issue:** DarkTheme.xaml defines no implicit `CheckBox` style. The WPF default CheckBox template uses `SystemColors.ControlTextBrush` for its foreground, which is typically black or dark gray. On the dark `BrushBackgroundPrimary` (#0E131A) or `BrushVoid` (#080B10) backgrounds used throughout the app, un-styled CheckBox labels are nearly invisible.

Affected CheckBox instances:
- `SessionView.xaml:54` -- `<CheckBox IsChecked="{Binding AutoCapture}" ...>` (no Foreground set)
- `SessionView.xaml:144` -- `<CheckBox Content="Anonymize" ... Foreground="...">` (HAS Foreground, OK)

**Fix:** Add an implicit CheckBox style to DarkTheme.xaml:
```xml
<Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="{StaticResource BrushTextSecondary}" />
    <Setter Property="FontFamily" Value="{StaticResource FontPrimary}" />
    <Setter Property="FontSize" Value="12" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
</Style>
```
And also add an explicit `Foreground` on the SessionView `AutoCapture` CheckBox as a belt-and-suspenders fix:
```xml
<CheckBox IsChecked="{Binding AutoCapture}" VerticalAlignment="Center" Margin="0,0,6,0"
          Foreground="{StaticResource BrushTextSecondary}" />
```

---

### CR-05: Missing Implicit ProgressBar Style

**File:** `src/QADeviceTool.App/Themes/DarkTheme.xaml` and `src/QADeviceTool.App/Views/AppManagementView.xaml:128`, `src/QADeviceTool.App/Views/DeepLinkView.xaml:97`

**Issue:** DarkTheme defines no implicit `ProgressBar` style. The default WPF ProgressBar uses system colors (green highlight on light gray track) which clash with the neo-industrial dark theme. Both AppManagementView and DeepLinkView use `<ProgressBar>` without a `Style` attribute.

**Fix:** Add an implicit ProgressBar style to DarkTheme.xaml:
```xml
<Style TargetType="ProgressBar">
    <Setter Property="Background" Value="{StaticResource BrushVoid}" />
    <Setter Property="Foreground" Value="{StaticResource BrushCyan}" />
    <Setter Property="BorderBrush" Value="{StaticResource BrushBorder}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Height" Value="4" />
</Style>
```

---

### CR-06: Missing Implicit RadioButton Style Breaks Group Behavior

**File:** `src/QADeviceTool.App/Themes/DarkTheme.xaml` and `src/QADeviceTool.App/MainWindow.xaml:157-216`

**Issue:** The `NavButton` style (for RadioButton) sets `GroupName="Navigation"` (line 574 of DarkTheme.xaml). This is correct for grouping. However, there is NO implicit `RadioButton` style defined in the theme. If any view adds a RadioButton outside the navigation bar without an explicit style, it will render with system default appearance. Additionally, the NavButton's ControlTemplate completely replaces the RadioButton template, so the built-in `GroupName` property still works via the logical tree. This is not a crash but is a theme completeness gap.

More critically, if `GroupName="Navigation"` is set in the style, adding another RadioButton with `GroupName="Navigation"` anywhere else (e.g., inside a DataTemplate in a view) would create unintended mutual exclusion with the sidebar navigation buttons.

**Fix:** Remove `GroupName` from the `NavButton` style setter. Use a wrapping container to enforce exclusivity instead, or set GroupName on each RadioButton instance individually:
```xml
<!-- In DarkTheme.xaml, NavButton style: remove GroupName setter -->
<Style x:Key="NavButton" TargetType="RadioButton">
    <!-- Remove: <Setter Property="GroupName" Value="Navigation" /> -->
    ...
</Style>

<!-- In MainWindow.xaml, wrap nav RadioButtons in a GroupBox or use a parent element -->
<StackPanel>
    <RadioButton Style="{StaticResource NavButton}" GroupName="MainNav" ... />
    ...
</StackPanel>
```

---

## Warnings

### WR-01: FileExplorerView Cross-DataContext Binding Fragile

**File:** `src/QADeviceTool.App/Views/FileExplorerView.xaml:30`

**Issue:** The ComboBox ItemsSource uses a cross-bound path:
```xml
ItemsSource="{Binding DataContext.DeviceVM.Devices, RelativeSource={RelativeSource AncestorType=Window}}"
```
This hardcodes the assumption that the parent `Window` always has `MainViewModel` as its DataContext with a `DeviceVM` property. If this UserControl is ever hosted in a different Window, or if the visual tree is disconnected (as can happen with virtualization or deferred loading), the binding resolves to `null` and shows an empty ComboBox. Additionally, `RelativeSource AncestorType=Window` walks the visual tree, which may not find a Window ancestor if the control is inside an Adorner, Popup, or ContextMenu.

**Fix:** Use a dedicated attached property or a static/application-level service to provide the device list. Alternatively, pass the device collection down through the ViewModel hierarchy so the FileExplorerViewModel has its own `AvailableDevices` property:
```csharp
// In FileExplorerViewModel
public ObservableCollection<DeviceInfo> AvailableDevices => _deviceMonitor.Devices;
```

---

### WR-02: FileExplorerView SelectedDevice Binding Cross-Context Inconsistency

**File:** `src/QADeviceTool.App/Views/FileExplorerView.xaml:31`

**Issue:** `SelectedItem="{Binding SelectedDevice}"` binds to `FileExplorerViewModel.SelectedDevice`, but the ItemsSource (line 30) comes from `MainViewModel.DeviceVM.Devices`. If `FileExplorerViewModel.SelectedDevice` is a different object reference than the item in the shared `Devices` collection, the ComboBox will not highlight the selected item. This happens if the FileExplorerViewModel clones or re-fetches device objects.

**Fix:** Ensure `FileExplorerViewModel.OnDeviceSelected(DeviceInfo value)` stores the same object reference. Verify with:
```csharp
// In OnDeviceSelected
SelectedDevice = value; // Must be same reference, not a clone
```

---

### WR-03: VitalsView ToggleButton IsChecked OneWay Binding Fights Built-In Toggle

**File:** `src/QADeviceTool.App/Views/VitalsView.xaml:94-96`

**Issue:**
```xml
<ToggleButton.IsChecked>
    <Binding Path="IsPolling" Mode="OneWay" />
</ToggleButton.IsChecked>
```
The ToggleButton's `IsChecked` has a OneWay binding and a `Command="{Binding TogglePollingCommand}"`. When the user clicks the button, the default ToggleButton behavior attempts to flip `IsChecked` locally. The OneWay binding then pushes the ViewModel's current value back, potentially causing flicker (the button toggles locally, then snaps back to the ViewModel state before the Command has a chance to update the ViewModel). On slow UI threads, this produces a visible flash.

**Fix:** Either let ToggleButton use its default TwoWay binding and remove the Command (let the property change handler in the VM trigger the toggle logic), or use a regular Button styled as a toggle instead:
```xml
<!-- Option A: TwoWay, no Command -->
<ToggleButton IsChecked="{Binding IsPolling}" ... />

<!-- Option B: Button styled like a toggle -->
<Button Content="{Binding IsPolling, Converter={x:Static conv:BooleanToPlayPauseConverter.Instance}}"
        Command="{Binding TogglePollingCommand}" ... />
```

---

### WR-04: SessionView ContextMenu Opens on Empty Space with Wrong Selection

**File:** `src/QADeviceTool.App/Views/SessionView.xaml:67-77` and `src/QADeviceTool.App/Views/SessionView.xaml.cs:90-111`

**Issue:** The session ListBox has a ContextMenu (lines 71-77) and a `MouseRightButtonUp` handler (line 67/90) that uses `InputHitTest` + visual tree walking to select the item under the cursor. However, if the user right-clicks on empty space below the last session or on the scrollbar track, `InputHitTest` returns `null` or a non-item visual. The handler exits early without changing selection, leaving `_vm.SelectedSession` set to whatever was previously selected. The ContextMenu still opens, and "Delete Session" or "Open Directory" operates on the previously-selected (and possibly wrong) session.

**Fix:** Guard the context menu handlers against null selected item, and suppress the ContextMenu on empty areas:
```csharp
private void SessionList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
{
    // ... existing hit test logic ...
    // If no item found, deselect and suppress context menu
    if (!found)
    {
        listBox.SelectedItem = null;
        listBox.ContextMenu.IsOpen = false;
    }
}
```

---

### WR-05: CommandPaletteWindow Deactivated Fires During Close Causing Reentry

**File:** `src/QADeviceTool.App/Views/CommandPaletteWindow.xaml.cs:49,81-84`

**Issue:** `Deactivated += OnDeactivated` (line 49, constructor) fires `CloseWindow()` when the window loses focus. When `CloseWindow()` runs the fade-out animation and eventually calls `Close()`, the closing process may trigger additional `Deactivated` events. This causes `CloseWindow()` to be called a second time, which adds another `fadeOut.Completed` handler and schedules a second `Close()`. Calling `Close()` on an already-closing Window has undefined behavior (may throw or silently fail).

**Fix:** Add a reentry guard:
```csharp
private bool _isClosing;
private void CloseWindow()
{
    if (_isClosing) return;
    _isClosing = true;
    // ... rest of method ...
}
```

---

### WR-06: DeviceView Watermark Visibility Uses NullToBoolConverter -- Empty String Not Handled

**File:** `src/QADeviceTool.App/Views/DeviceView.xaml:168`

**Issue:** The watermark text for the IP address TextBox uses:
```xml
<DataTrigger Binding="{Binding WirelessIpAddress, Converter={x:Static conv:NullToBoolConverter.Instance}}" Value="False">
```
`NullToBoolConverter.Convert` returns `true` only for `value != null`. When the user clears the text field and it becomes `""` (empty string), the value is non-null, so the converter returns `true`, the DataTrigger does NOT fire, and the watermark remains `Collapsed`. The watermark disappears as soon as the user types and then deletes all characters.

**Fix:** Use a converter that also checks for empty strings, or use a `StringNullOrEmptyToVisibilityConverter`. Alternatively, add a second DataTrigger:
```xml
<DataTrigger Binding="{Binding WirelessIpAddress, Converter={x:Static conv:NullToBoolConverter.Instance}}" Value="False">
    <Setter Property="Visibility" Value="Visible" />
</DataTrigger>
<DataTrigger Binding="{Binding WirelessIpAddress.Length, FallbackValue=0}" Value="0">
    <Setter Property="Visibility" Value="Visible" />
</DataTrigger>
```

---

### WR-07: MacroView ListBox Missing ItemContainerStyle

**File:** `src/QADeviceTool.App/Views/MacroView.xaml:73`

**Issue:** The MacroListBox (line 73) does not specify an `ItemContainerStyle`. Without `BasedOn="{StaticResource DarkListBoxItem}"`, the ListBox items use the default WPF ListBoxItem style (white/light background on selection, system colors). This is inconsistent with every other ListBox in the application which explicitly sets the container style.

**Fix:**
```xml
<ListBox ItemsSource="{Binding Macros}" SelectedItem="{Binding SelectedMacro}"
         Style="{StaticResource DarkListBox}">
    <ListBox.ItemContainerStyle>
        <Style TargetType="ListBoxItem" BasedOn="{StaticResource DarkListBoxItem}" />
    </ListBox.ItemContainerStyle>
    ...
</ListBox>
```

---

### WR-08: StressTestView EventCount/Seed/ThrottleMs TextBoxes Missing UpdateSourceTrigger

**File:** `src/QADeviceTool.App/Views/StressTestView.xaml:17,19,21`

**Issue:** Most numeric TextBoxes bind without `UpdateSourceTrigger=PropertyChanged`:
```xml
<TextBox Text="{Binding EventCount}" ... />
<TextBox Text="{Binding Seed}" ... />
<TextBox Text="{Binding ThrottleMs}" ... />
```
WPF TextBox defaults to `UpdateSourceTrigger=LostFocus`. If the user types a value and immediately clicks "Run Monkey" without tabbing out first, the ViewModel still has the old value. The monkey test runs with stale configuration.

**Fix:** Add `UpdateSourceTrigger=PropertyChanged` to all configuration TextBoxes:
```xml
<TextBox Text="{Binding EventCount, UpdateSourceTrigger=PropertyChanged}" ... />
<TextBox Text="{Binding Seed, UpdateSourceTrigger=PropertyChanged}" ... />
<TextBox Text="{Binding ThrottleMs, UpdateSourceTrigger=PropertyChanged}" ... />
```

---

### WR-09: SessionView IsChecked TwoWay Binding on ParseToggle -- InverseBoolConverter Idempotent but Fragile

**File:** `src/QADeviceTool.App/Views/SessionView.xaml:213`

**Issue:** The Parse toggle uses `InverseBoolConverter` on `IsChecked`:
```xml
IsChecked="{Binding IsRawMode, Converter={x:Static conv:InverseBoolConverter.Instance}}"
```
Because both `Convert` and `ConvertBack` implement `!b`, the double negation makes the toggle functionally correct (`IsChecked == !IsRawMode`). However, `ConvertBack` receives `bool?` (nullable) from `ToggleButton.IsChecked` but only handles `bool` via `value is bool b`. If `IsChecked` ever becomes `null` (indeterminate state, currently impossible since `IsThreeState` defaults to false), `ConvertBack` returns `false`, which would silently clobber `IsRawMode` to `false`. While not currently exploitable, this is brittle -- a future style change enabling three-state would corrupt the ViewModel.

**Fix:** Use a dedicated `InverseBoolToNullableBoolConverter` or handle the nullable case:
```csharp
public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
{
    if (value is bool b) return !b;
    return Binding.DoNothing; // Don't update source if value is indeterminate
}
```

---

### WR-10: SessionView ScrollToEndRequested Handler Not Firing After DataContext Change Without Garbage Collection

**File:** `src/QADeviceTool.App/Views/SessionView.xaml.cs:28-36`

**Issue:** The `DataContextChanged` handler unsubscribes the old event and subscribes the new one. This is correct. However, if the `DataContext` is set to the SAME `SessionViewModel` instance multiple times (e.g., during a theme switch or layout pass), `_vm` and the new `DataContext` are reference-equal. The handler unsubscribes and re-subscribes, which is a no-op. No bug here.

However, the `DataContextChanged` lambda captures `this` (the UserControl). The UserControl's lifetime is tied to its parent -- if the parent holds a strong reference (via visual tree), the UserControl is never collected. The lambda is added to `DataContextChanged`, which is a CLR event on the UserControl itself. This is a self-referencing cycle that prevents GC only while the UserControl is in the visual tree, which is expected. Not a leak, but worth documenting.

---

## Info

### IN-01: CommandPaletteWindow Icons Render as Missing Glyphs (tofu)

**File:** `src/QADeviceTool.App/Views/CommandPaletteWindow.xaml:94-95` and `src/QADeviceTool.App/MainWindow.xaml.cs:33-48`

**Issue:** The Icon TextBlock uses `FontFamily="Segoe MDL2 Assets"`, but the registered commands pass emoji characters (U+1F50D "🔍", U+1F4F1 "📱", etc.). The Segoe MDL2 Assets font does not contain emoji glyphs. These characters will render as tofu (□). The correct font family for emoji on Windows is `"Segoe UI Emoji"`.

**Fix:** Either use Segoe MDL2 Assets codepoints (e.g., `&#xE721;` for the search icon) or change the FontFamily:
```xml
<!-- Option A: Use MDL2 glyphs in AddCommand calls -->
_commandPalette.AddCommand("nav:dashboard", "Go to Dashboard", "Navigate to dashboard view", "", "Ctrl+1");

<!-- Option B: Change font -->
<TextBlock ... FontFamily="Segoe UI Emoji, Segoe MDL2 Assets" />
```

---

### IN-02: Incomplete Converter Registration -- BooleanToVisibilityConverter Defined Twice

**File:** `src/QADeviceTool.App/App.xaml:10` and `src/QADeviceTool.App/Views/SessionView.xaml:9`

**Issue:** `BooleanToVisibilityConverter` is registered globally in App.xaml as `"BooleanToVisibilityConverter"`. SessionView.xaml also registers `BooleanToVisibilityConverter` locally as `"BoolToVis"`. Having two instances wastes a negligible amount of memory but creates confusion: which key does a view use? Currently both keys work in their respective scopes, but if a view uses `{StaticResource BooleanToVisibilityConverter}` while a local style shadowed it, the behavior could be unexpected.

**Fix:** Remove the local registration in SessionView and use the global resource:
```xml
<!-- Remove from SessionView.Resources: -->
<!-- <BooleanToVisibilityConverter x:Key="BoolToVis" /> -->
```

---

### IN-03: Deviant Resource Key Names in Theme -- Inconsistent Naming

**File:** `src/QADeviceTool.App/Themes/DarkTheme.xaml:79-113`

**Issue:** The theme defines two naming conventions:
1. Neo-industrial: `BrushVoid`, `BrushBase`, `BrushSurface`, `BrushElevated`, `BrushBorder`, `BrushCyan`, `BrushTextPrimary`, etc.
2. Compatibility aliases: `BrushBackgroundPrimary`, `BrushBackgroundSurface`, `BrushSeparator`, `BrushAccent`, `BrushDanger`, `BrushWarning`, `BrushPrimary`, etc.

Views mix both conventions freely. `BrushBackgroundPrimary` (line 80) is an alias for `BrushBase`. `BrushAccent` (line 86) is an alias for `BrushCyan`. `BrushDanger` (line 89) is an alias for `BrushRed`. This dual naming creates confusion about which key to use and which semantic meaning is intended. New developers may use `BrushAccent` and `BrushCyan` interchangeably without realizing they are the same brush.

**Fix:** Decide on ONE convention. If the neo-industrial names are the canonical ones, migrate all view references and remove or `[Obsolete]` the compatibility aliases.

---

### IN-04: ComboBox Default Style Missing MaxDropDownHeight

**File:** `src/QADeviceTool.App/Themes/DarkTheme.xaml:392-487`

**Issue:** The implicit ComboBox style does not set `MaxDropDownHeight`. On high-DPI displays or with many items, the dropdown Popup may extend beyond the screen boundary, clipping items from view.

**Fix:** Add a reasonable default:
```xml
<Setter Property="MaxDropDownHeight" Value="300" />
```

---

### IN-05: WindowChrome + AllowsTransparency Anti-Pattern

**File:** `src/QADeviceTool.App/MainWindow.xaml:12-14,18-20`

**Issue:** The MainWindow uses both `AllowsTransparency="True"` (line 14) and `<WindowChrome>` (line 18). `AllowsTransparency` forces software rendering (disabling hardware acceleration), which degrades rendering performance, especially with animations, video (screen mirroring), and high-frequency log updates. `WindowChrome` is designed to provide custom chrome WITHOUT `AllowsTransparency`. The combination works on older Windows versions but causes visual artifacts, flickering during resize, and increased CPU usage on Windows 10+.

**Fix:** Remove `AllowsTransparency="True"` and set the Window background to a solid color:
```xml
<Window ... 
        Background="{StaticResource BrushVoid}"
        WindowStyle="None"
        ResizeMode="CanResize">
    <WindowChrome.WindowChrome>
        <WindowChrome CaptionHeight="0" ResizeBorderThickness="4" CornerRadius="8"
                      GlassFrameThickness="0" UseAeroCaptionButtons="False" />
    </WindowChrome.WindowChrome>
```
Note: You can achieve rounded corners via `WindowChrome.CornerRadius` on Windows 11, avoiding the need for `AllowsTransparency`.

---

### IN-06: StressTestView XAML Indentation Inconsistency

**File:** `src/QADeviceTool.App/Views/StressTestView.xaml:37-39`

**Issue:** The ListBox.ItemContainerStyle opening tag is on the same line as the parent tag, breaking the indentation pattern used across all other views:
```xml
<ListBox ItemsSource="{Binding Devices}"
         SelectedItem="{Binding SelectedDevice}"
         Style="{StaticResource DarkListBox}"
         ScrollViewer.VerticalScrollBarVisibility="Auto">
<ListBox.ItemContainerStyle>          <!-- Indented to same level as ListBox, not inside -->
    <Style ... />
</ListBox.ItemContainerStyle>
```
This occurs in: StressTestView.xaml, DeviceView.xaml:42, AppManagementView.xaml:38, ShellView.xaml:41, VitalsView.xaml:41, DeepLinkView.xaml:41. It is cosmetic but suggests the XAML was copy-pasted without reformatting and could hide real nesting issues during manual review.

**Fix:** Reformat all instances to place the child element inside the parent with proper indentation.

---

### IN-07: OnSelectedDeviceChanged Propagates to All Child VMs Even When Not Visible

**File:** `src/QADeviceTool.App/ViewModels/MainViewModel.cs:127-142`

**Issue:** `OnSelectedDeviceChanged` calls `OnDeviceSelected(value)` on ALL 10 child ViewModels regardless of which one is currently displayed. This is premature work -- 9 of 10 ViewModels are processing a device selection event that is irrelevant to them. While each `OnDeviceSelected` method is presumably light (setting a property), the cumulative cost includes property-change notifications, UI binding updates on hidden views, and potential side effects in non-visible ViewModels.

**Fix:** Only propagate to the currently active ViewModel:
```csharp
partial void OnSelectedDeviceChanged(DeviceInfo? value)
{
    if (value == null) return;
    if (CurrentView is DashboardViewModel dvm) dvm.OnDeviceSelected(value);
    else if (CurrentView is SessionViewModel svm) svm.OnDeviceSelected(value);
    // ... only the active one
}
```
Or, make each child ViewModel subscribe to shared device state via a messenger/event aggregator.

---

### IN-08: App.xaml.cs EarlyLog Uses DateTime.Now Without Capturing Once

**File:** `src/QADeviceTool.App/App.xaml.cs:22`

**Issue:** `DateTime.Now` is called once per log line:
```csharp
var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
```
This is fine (single call). No midnight boundary bug here unlike the PathHelper issue found in the previous review. This is informational only -- the pattern is correct in this file.

---

### IN-09: Dead Code -- FeatureFlags Properties Never Set to True

**File:** `src/QADeviceTool.App/FeatureFlags.cs:12-17`

**Issue:** `AiLogAnalysis` and `MultiSelect` are both `false` with no mechanism in the codebase to set them to `true`. The feature flags exist as dead code. The `MainWindow.xaml.cs:51-59` checks `FeatureFlags.AiLogAnalysis` and `FeatureFlags.MultiSelect`, but since they are always `false`, the corresponding command palette entries are never registered.

**Fix:** Either wire up a configuration file/settings UI to toggle these flags, or remove the dead code. If these are for future use, add a TODO comment:
```csharp
/// <summary>
/// Enable AI-powered log analysis and anomaly detection.
/// TODO: Wire to settings/preferences
/// </summary>
public static bool AiLogAnalysis { get; set; } = false;
```

---

_Reviewed: 2026-05-07T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
