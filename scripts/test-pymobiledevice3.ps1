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
    if ($ExpectedText -and -not $output.Contains($ExpectedText)) {
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
