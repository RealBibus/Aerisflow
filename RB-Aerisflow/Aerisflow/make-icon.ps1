Add-Type -AssemblyName System.Drawing

function New-FlakeBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 27, 42, 74))
    $g.FillEllipse($bg, 0, 0, $size - 1, $size - 1)

    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.0, $size * 0.085))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $c = $size / 2.0
    $R = $size * 0.36
    for ($k = 0; $k -lt 6; $k++) {
        $a = $k * [Math]::PI / 3
        $tx = $c + $R * [Math]::Cos($a); $ty = $c + $R * [Math]::Sin($a)
        $g.DrawLine($pen, $c, $c, $tx, $ty)
        foreach ($t in @(0.6, 0.85)) {
            $bx = $c + $R * $t * [Math]::Cos($a); $by = $c + $R * $t * [Math]::Sin($a)
            $len = $R * 0.3
            foreach ($s in @(-0.6, 0.6)) {
                $ex = $bx + $len * [Math]::Cos($a + $s); $ey = $by + $len * [Math]::Sin($a + $s)
                $g.DrawLine($pen, $bx, $by, $ex, $ey)
            }
        }
    }
    $wb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $cr = $size * 0.08
    $g.FillEllipse($wb, $c - $cr, $c - $cr, $cr * 2, $cr * 2)

    $g.Dispose(); $pen.Dispose(); $bg.Dispose(); $wb.Dispose()
    return $bmp
}

$sizes = @(256, 48, 32, 16)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-FlakeBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($s, $ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.Collections.Generic.List[byte]
$out.AddRange([BitConverter]::GetBytes([uint16]0))
$out.AddRange([BitConverter]::GetBytes([uint16]1))
$out.AddRange([BitConverter]::GetBytes([uint16]$frames.Count))

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $out.Add([byte]$dim); $out.Add([byte]$dim)
    $out.Add([byte]0); $out.Add([byte]0)
    $out.AddRange([BitConverter]::GetBytes([uint16]1))
    $out.AddRange([BitConverter]::GetBytes([uint16]32))
    $out.AddRange([BitConverter]::GetBytes([uint32]$data.Length))
    $out.AddRange([BitConverter]::GetBytes([uint32]$offset))
    $offset += $data.Length
}
foreach ($f in $frames) { $out.AddRange($f[1]) }

$path = Join-Path $PSScriptRoot "Aerisflow.ico"
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
Write-Host "icon written: $path"