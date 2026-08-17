# Builds assets/bosun.ico from two sources:
#   >= 48px : the crossed fouled anchors (assets/bosun-icon-source.png) -- Bosun's identity mark
#   <  48px : the single anchor, drawn from the same vector geometry the tray uses, because the
#             crossed pair is illegible below ~48px (measured; see AnchorMarkGeometry's remarks)
# Both are knocked out in white on a navy rounded square, which is the only treatment that stays
# readable on BOTH a light Explorer background and a dark taskbar (measured; navy-on-transparent
# nearly vanishes on the dark taskbar).
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$repo = "C:\Users\Barry\Development\bosun"
$src  = "$repo\assets\bosun-icon-source.png"
$ico  = "$repo\assets\bosun.ico"

$navy  = [System.Windows.Media.Color]::FromRgb(18, 58, 90)
$navyBrush = New-Object System.Windows.Media.SolidColorBrush($navy); $navyBrush.Freeze()
$white = [System.Windows.Media.Brushes]::White

function New-SingleAnchorDrawing {
    param($Brush)
    $g = New-Object System.Windows.Media.DrawingGroup
    $ring = New-Object System.Windows.Media.GeometryDrawing
    $ring.Geometry = New-Object System.Windows.Media.EllipseGeometry((New-Object System.Windows.Point(50,13)), 8, 8)
    $ring.Pen = New-Object System.Windows.Media.Pen($Brush, 7)
    $g.Children.Add($ring)
    $shank = New-Object System.Windows.Media.GeometryDrawing
    $shank.Geometry = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(45.5, 20, 9, 58)))
    $shank.Brush = $Brush; $g.Children.Add($shank)
    $stock = New-Object System.Windows.Media.GeometryDrawing
    $stock.Geometry = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(27, 29, 46, 8)))
    $stock.Brush = $Brush; $g.Children.Add($stock)
    $crown = New-Object System.Windows.Media.GeometryDrawing
    $crown.Geometry = [System.Windows.Media.Geometry]::Parse('M 18,50 C 18,74 32,86 50,86 C 68,86 82,74 82,50')
    $pen = New-Object System.Windows.Media.Pen($Brush, 9); $pen.StartLineCap='Round'; $pen.EndLineCap='Round'
    $crown.Pen = $pen; $g.Children.Add($crown)
    foreach ($tri in @('M 6,36 L 27,47 L 16,58 Z', 'M 94,36 L 73,47 L 84,58 Z')) {
        $f = New-Object System.Windows.Media.GeometryDrawing
        $f.Geometry = [System.Windows.Media.Geometry]::Parse($tri)
        $f.Brush = $Brush; $g.Children.Add($f)
    }
    return $g
}

# Load the crossed mark once, and build a white version of it via an opacity mask so the alpha
# shape is preserved but the colour becomes white.
$srcBmp = New-Object System.Windows.Media.Imaging.BitmapImage
$srcBmp.BeginInit(); $srcBmp.UriSource = [Uri]$src; $srcBmp.CacheOption = 'OnLoad'; $srcBmp.EndInit()

function New-IconPng {
    param([int]$Size)

    $v = New-Object System.Windows.Media.DrawingVisual
    $ctx = $v.RenderOpen()

    # Navy rounded square.
    $radius = $Size * 0.22
    $rect = New-Object System.Windows.Rect(0, 0, $Size, $Size)
    $ctx.DrawRoundedRectangle($navyBrush, $null, $rect, $radius, $radius)

    $inset = $Size * 0.15
    $inner = $Size - ($inset * 2)

    if ($Size -ge 48) {
        # Crossed anchors, forced to white via an ImageBrush used as an opacity mask.
        $brush = New-Object System.Windows.Media.ImageBrush($srcBmp)
        $brush.Stretch = 'Uniform'
        $ctx.PushOpacityMask($brush)
        $ctx.DrawRectangle($white, $null, (New-Object System.Windows.Rect($inset, $inset, $inner, $inner)))
        $ctx.Pop()
    } else {
        $scale = $inner / 100.0
        $ctx.PushTransform((New-Object System.Windows.Media.TranslateTransform($inset, $inset)))
        $ctx.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $ctx.DrawDrawing((New-SingleAnchorDrawing -Brush $white))
        $ctx.Pop(); $ctx.Pop()
    }
    $ctx.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($v)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = New-IconPng -Size $s }

# --- assemble the .ico ---
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)               # reserved
$bw.Write([UInt16]1)               # type: icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))   # width  (0 == 256)
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))   # height
    $bw.Write([Byte]0)             # palette count
    $bw.Write([Byte]0)             # reserved
    $bw.Write([UInt16]1)           # colour planes
    $bw.Write([UInt16]32)          # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $bw.Close(); $fs.Close()

"wrote $ico ({0:N0} bytes, {1} sizes: {2})" -f (Get-Item $ico).Length, $sizes.Count, ($sizes -join ', ')

# Preview sheet so the result can be inspected on both backgrounds.
$prev = "$env:TEMP\claude\C--Users-Barry-Development-bosun\9afbabce-e84d-44be-b67e-e2da8cdcd737\scratchpad\icon-preview\final-ico.png"
$sheet = New-Object System.Windows.Media.DrawingVisual
$sc = $sheet.RenderOpen()
$sc.DrawRectangle([System.Windows.Media.Brushes]::WhiteSmoke, $null, (New-Object System.Windows.Rect(0,0,600,160)))
$sc.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(32,32,32))), $null, (New-Object System.Windows.Rect(0,160,600,160)))
[System.Windows.Media.RenderOptions]::SetBitmapScalingMode($sheet, 'NearestNeighbor')
foreach ($band in @(0, 160)) {
    $x = 20
    foreach ($s in $sizes) {
        $ms = New-Object System.IO.MemoryStream(,$pngs[$s])
        $bi = New-Object System.Windows.Media.Imaging.BitmapImage
        $bi.BeginInit(); $bi.StreamSource = $ms; $bi.CacheOption = 'OnLoad'; $bi.EndInit()
        $draw = [Math]::Max($s, 48)
        $sc.DrawImage($bi, (New-Object System.Windows.Rect($x, ($band + 30), $draw, $draw)))
        $x += $draw + 16
    }
}
$sc.Close()
$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(600, 320, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($sheet)
$enc2 = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc2.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$f2 = [System.IO.File]::Create($prev); $enc2.Save($f2); $f2.Close()
"wrote preview: final-ico.png"
