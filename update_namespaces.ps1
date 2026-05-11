Get-ChildItem -Path 'D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App' -Recurse -Include *.cs,*.xaml | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    if ($content -match 'QADeviceTool') {
        $newContent = $content -replace 'QADeviceTool', 'LogPro'
        Set-Content $_.FullName -Value $newContent -NoNewline
        Write-Host "Updated: $($_.Name)"
    }
}

Write-Host "=== DONE ==="
Write-Host "Total files with QADeviceTool replaced"