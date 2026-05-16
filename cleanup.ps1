$source = "D:\OpenCode\QAQC\QADeviceTool\publish"
$keep = @("QADeviceTool_Setup_v2.7.0.exe", "QADeviceTool_Portable_v2.7.0.zip")

Get-ChildItem -Path $source -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
    $dest = Join-Path $source "_dist"
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }
    Move-Item -Path $_.FullName -Destination $dest -Force
    Write-Host "Moved: $($_.Name)"
}

Get-ChildItem -Path $source -Directory | ForEach-Object {
    $dest = Join-Path $source "_dist"
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }
    Move-Item -Path $_.FullName -Destination $dest -Force
    Write-Host "Moved dir: $($_.Name)"
}

Write-Host "=== Final publish folder ==="
Get-ChildItem -Path $source | Select-Object Name