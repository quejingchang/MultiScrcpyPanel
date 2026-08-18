# generate_app_icon.ps1
# 生成灯泡图标 lightbulb.ico（纯 GDI+，无外部依赖）。
# 由 csproj 的 GenerateAppIcon 生成目标在构建前调用；也可单独运行调试。
param(
    [string]$OutFile = $(Join-Path $PSScriptRoot 'lightbulb.ico')
)

Add-Type -AssemblyName System.Drawing

function New-BulbBitmap {
    param([int]$S)

    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $cx = $S * 0.5

    $neckTop = $S * 0.60
    $baseTop = $S * 0.70
    $baseBot = $S * 0.86

    # ---- 螺纹底座 + 颈部（金属灰渐变）----
    $grayTop = [System.Drawing.Color]::FromArgb(255, 190, 196, 204)
    $grayBot = [System.Drawing.Color]::FromArgb(255, 138, 144, 152)
    $grayBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $baseTop)),
        (New-Object System.Drawing.PointF(0, $baseBot)),
        $grayTop, $grayBot)

    $basePts = @(
        (New-Object System.Drawing.PointF($cx - $S * 0.12,  $baseTop)),
        (New-Object System.Drawing.PointF($cx + $S * 0.12,  $baseTop)),
        (New-Object System.Drawing.PointF($cx + $S * 0.155, $baseBot)),
        (New-Object System.Drawing.PointF($cx - $S * 0.155, $baseBot))
    )
    $g.FillPolygon($grayBrush, $basePts)

    $neckPts = @(
        (New-Object System.Drawing.PointF($cx - $S * 0.105, $neckTop)),
        (New-Object System.Drawing.PointF($cx + $S * 0.105, $neckTop)),
        (New-Object System.Drawing.PointF($cx + $S * 0.12,  $baseTop)),
        (New-Object System.Drawing.PointF($cx - $S * 0.12,  $baseTop))
    )
    $g.FillPolygon($grayBrush, $neckPts)

    # 螺纹线
    $threadPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 95, 101, 110), [Math]::Max(1, $S * 0.012))
    foreach ($ty in @(0.735, 0.775, 0.815, 0.852)) {
        $y = $S * $ty
        $hw = $S * (0.12 + 0.035 * (($ty - 0.70) / 0.16))
        $g.DrawLine($threadPen, $cx - $hw, $y, $cx + $hw, $y)
    }

    # ---- 玻璃灯泡（琥珀渐变）----
    $gx = $cx
    $gy = $S * 0.37
    $gr = $S * 0.27
    $glassTop = [System.Drawing.Color]::FromArgb(255, 255, 224, 138)
    $glassBot = [System.Drawing.Color]::FromArgb(255, 255, 180, 61)
    $glassBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $gy - $gr)),
        (New-Object System.Drawing.PointF(0, $gy + $gr)),
        $glassTop, $glassBot)
    $g.FillEllipse($glassBrush, $cx - $gr, $gy - $gr, $gr * 2, $gr * 2)

    # 高光（左上柔光）
    $hl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120, 255, 255, 255))
    $g.FillEllipse($hl, $cx - $gr * 0.95, $gy - $gr * 1.05, $gr * 0.9, $gr * 1.2)

    # ---- 灯丝（外发光 + 内核）----
    $filPts = @(
        (New-Object System.Drawing.PointF($cx - $S * 0.08, $S * 0.47)),
        (New-Object System.Drawing.PointF($cx - $S * 0.08, $S * 0.40)),
        (New-Object System.Drawing.PointF($cx,            $S * 0.33)),
        (New-Object System.Drawing.PointF($cx + $S * 0.08, $S * 0.40)),
        (New-Object System.Drawing.PointF($cx + $S * 0.08, $S * 0.47))
    )
    $glowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 255, 150, 20), [Math]::Max(2, $S * 0.05))
    $g.DrawLines($glowPen, $filPts)
    $corePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 138, 0), [Math]::Max(1, $S * 0.022))
    $g.DrawLines($corePen, $filPts)

    # ---- 描边 ----
    $ow = [Math]::Max(1, $S * 0.025)
    $glassOutline = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 122, 78, 10), $ow)
    $g.DrawEllipse($glassOutline, $cx - $gr, $gy - $gr, $gr * 2, $gr * 2)
    $metalOutline = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 74, 80, 88), $ow)
    $g.DrawPolygon($metalOutline, $basePts)
    $g.DrawPolygon($metalOutline, $neckPts)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object System.Collections.ArrayList

foreach ($sz in $sizes) {
    $bmp = New-BulbBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $null = $frames.Add(@($sz, $ms.ToArray()))
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $sz = $f[0]
    $data = $f[1]
    $bw.Write([byte](if ($sz -eq 256) { 0 } else { $sz }))
    $bw.Write([byte](if ($sz -eq 256) { 0 } else { $sz }))
    $bw.Write([byte]0)   # 调色板颜色数
    $bw.Write([byte]0)   # 保留
    $bw.Write([uint16]1) # 平面数
    $bw.Write([uint16]32) # 位深
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) {
    $bw.Write($f[1])
}
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
$out.Dispose()
Write-Host "生成图标: $OutFile ($([System.IO.File]::ReadAllBytes($OutFile).Length) 字节, $($frames.Count) 帧)"
