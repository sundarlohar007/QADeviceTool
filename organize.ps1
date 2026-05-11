$source = "D:\OpenCode\QAQC\QADeviceTool\publish"
$dest = "D:\OpenCode\QAQC\QADeviceTool\publish\runtime"

if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }

$keep = @("QADeviceTool.exe", "QADeviceTool.dll", "setup.iss")

Get-ChildItem -Path $source -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
    Move-Item -Path $_.FullName -Destination $dest -Force
    Write-Host "Moved: $($_.Name)"
}

Get-ChildItem -Path $source -Directory | ForEach-Object {
    Move-Item -Path $_.FullName -Destination $dest -Force
    Write-Host "Moved dir: $($_.Name)"
}

Write-Host "Done."
Get-ChildItem -Path $source | Select-Object Name