Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'

# Dark background
$g.Clear([System.Drawing.Color]::FromArgb(30, 30, 30))

# Green circle
$brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 212, 86))
$g.FillEllipse($brush, 20, 20, 216, 216)

# White LP text
$font = New-Object System.Drawing.Font('Segoe UI', 72, [System.Drawing.FontStyle]::Bold)
$textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'
$sf.LineAlignment = 'Center'
$rect = New-Object System.Drawing.RectangleF(0, 0, 256, 256)
$g.DrawString('LP', $font, $textBrush, $rect, $sf)

$g.Dispose()
$font.Dispose()

# Save as PNG
$bmp.Save('D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Assets\LogProIcon.png', [System.Drawing.Imaging.ImageFormat]::Png)

# Copy as ICO placeholder (not a real ICO but will work for now)
Copy-Item 'D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Assets\LogProIcon.png' 'D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Assets\LogProIcon.ico'

Write-Host 'Icon created successfully'