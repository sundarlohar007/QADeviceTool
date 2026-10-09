param(
    [string]$Executable = "src/LogPro.App/tools/pymobiledevice3/pymobiledevice3.exe"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "pymobiledevice3 executable not found: $Executable"
}

function Assert-CommandHelp([string[]]$Arguments, [string]$ExpectedText) {
    $output = (& $Executable @Arguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "pymobiledevice3 $($Arguments -join ' ') failed: $output"
    }
    # Typer versions vary the casing of argument metavars (REMOTE_FILE/remote_file).
    # Assert the command/argument exists without coupling the test to presentation.
    if ($ExpectedText -and $output.IndexOf($ExpectedText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "pymobiledevice3 $($Arguments -join ' ') is missing '$ExpectedText' in its help output."
    }
}

Assert-CommandHelp @("version") "9.12.0"
Assert-CommandHelp @("--help") "afc"
Assert-CommandHelp @("syslog", "live", "--help") "--process-name"
Assert-CommandHelp @("afc", "ls", "--help") "REMOTE_FILE"
Assert-CommandHelp @("afc", "pull", "--help") "--ignore-errors"
Assert-CommandHelp @("crash", "pull", "--help") "--remote-file"
Assert-CommandHelp @("developer", "screenshot", "--help") "OUT"
Write-Host "pymobiledevice3 command smoke tests passed."

# Run the frozen entrypoint through redirected pipes, as device capture does.
$start = [Diagnostics.ProcessStartInfo]::new($Executable, '--logpro-encoding-check')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.Encoding]::UTF8
$start.StandardErrorEncoding = [Text.Encoding]::UTF8
$process = [Diagnostics.Process]::Start($start)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Unicode probe timed out' }
    $expected = "LogPro Unicode: $([char]0x202f) $([char]::ConvertFromUtf32(0x1f4f1)) $([char]0x65e5)$([char]0x672c)$([char]0x8a9e)"
    if ($process.ExitCode -ne 0 -or $stdout.Result.Trim() -ne $expected -or $stderr.Result.Trim() -ne $expected) {
        throw 'Bundled runtime cannot preserve Unicode on stdout/stderr'
    }
} finally { $process.Dispose() }
