# Generates app.ico (multi-resolution TV icon) for DvbTv. ASCII-only (PS 5.1 safe).
Add-Type -AssemblyName System.Drawing

function Get-RoundedRect($rect, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2.0
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-TvPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $cx = $s * 0.5

    # ---- antennas ----
    $penCol = [System.Drawing.Color]::FromArgb(255, 120, 130, 150)
    $pen = New-Object System.Drawing.Pen($penCol, [single]($s * 0.05))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, [single]$cx, [single]($s*0.36), [single]($s*0.30), [single]($s*0.10))
    $g.DrawLine($pen, [single]$cx, [single]($s*0.36), [single]($s*0.70), [single]($s*0.10))
    $tip = $s * 0.045
    $tipBrush = New-Object System.Drawing.SolidBrush($penCol)
    $g.FillEllipse($tipBrush, [single]($s*0.30-$tip), [single]($s*0.10-$tip), [single]($tip*2), [single]($tip*2))
    $g.FillEllipse($tipBrush, [single]($s*0.70-$tip), [single]($s*0.10-$tip), [single]($tip*2), [single]($tip*2))

    # ---- TV body (rounded) ----
    $pad = $s * 0.08
    $bodyTop = $s * 0.34
    $bodyBot = $s * 0.84
    $body = New-Object System.Drawing.RectangleF($pad, $bodyTop, ($s - 2*$pad), ($bodyBot - $bodyTop))
    $bodyPath = Get-RoundedRect $body ([single]($s*0.07))
    $bodyBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 35, 35, 45))
    $g.FillPath($bodyBrush, $bodyPath)

    # ---- screen (gradient) ----
    $sIn = $s * 0.045
    $screen = New-Object System.Drawing.RectangleF(($pad+$sIn), ($bodyTop+$sIn), ($s - 2*$pad - 2*$sIn), ($bodyBot - $bodyTop - 2*$sIn))
    $screenPath = Get-RoundedRect $screen ([single]($s*0.035))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($screen, [System.Drawing.Color]::FromArgb(255,31,111,235), [System.Drawing.Color]::FromArgb(255,34,211,238), 50.0)
    $g.FillPath($grad, $screenPath)

    # ---- play triangle ----
    $tw = $screen.Width * 0.20
    $scx = $screen.X + $screen.Width * 0.5
    $scy = $screen.Y + $screen.Height * 0.5
    $pts = @(
        (New-Object System.Drawing.PointF(($scx - $tw*0.45), ($scy - $tw*0.6))),
        (New-Object System.Drawing.PointF(($scx - $tw*0.45), ($scy + $tw*0.6))),
        (New-Object System.Drawing.PointF(($scx + $tw*0.65), $scy))
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)

    # ---- feet ----
    $footPen = New-Object System.Drawing.Pen($bodyBrush.Color, [single]($s*0.045))
    $footPen.StartCap = 'Round'; $footPen.EndCap = 'Round'
    $g.DrawLine($footPen, [single]($s*0.32), [single]($bodyBot+$s*0.02), [single]($s*0.26), [single]($bodyBot+$s*0.08))
    $g.DrawLine($footPen, [single]($s*0.68), [single]($bodyBot+$s*0.02), [single]($s*0.74), [single]($bodyBot+$s*0.08))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

$sizes = 16,24,32,48,64,128,256
$pngs = @()
foreach ($sz in $sizes) {
    $p = New-TvPng $sz
    Write-Output ("size " + $sz + " -> png bytes: " + $p.Length)
    $pngs += ,$p
}

function Add-U16($list, [int]$v) { $list.Add([byte]($v -band 0xFF)); $list.Add([byte](($v -shr 8) -band 0xFF)) }
function Add-U32($list, [int]$v) { $list.Add([byte]($v -band 0xFF)); $list.Add([byte](($v -shr 8) -band 0xFF)); $list.Add([byte](($v -shr 16) -band 0xFF)); $list.Add([byte](($v -shr 24) -band 0xFF)) }

$n = $pngs.Count
$buf = New-Object 'System.Collections.Generic.List[byte]'
Add-U16 $buf 0   # reserved
Add-U16 $buf 1   # type = icon
Add-U16 $buf $n  # image count
$offset = 6 + 16*$n
for ($i = 0; $i -lt $n; $i++) {
    $sz = $sizes[$i]; $b = [byte[]]$pngs[$i]
    $w = if ($sz -ge 256) { 0 } else { $sz }
    $buf.Add([byte]$w); $buf.Add([byte]$w); $buf.Add([byte]0); $buf.Add([byte]0)
    Add-U16 $buf 1; Add-U16 $buf 32
    Add-U32 $buf $b.Length; Add-U32 $buf $offset
    $offset += $b.Length
}
for ($i = 0; $i -lt $n; $i++) { $buf.AddRange([byte[]]$pngs[$i]) }
[System.IO.File]::WriteAllBytes("C:\Claude\dvb_tv\app.ico", $buf.ToArray())
Write-Output ("app.ico written: " + ([System.IO.FileInfo]::new("C:\Claude\dvb_tv\app.ico").Length) + " bytes")
