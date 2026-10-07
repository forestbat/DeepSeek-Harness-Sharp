#requires -Version 7
# 生成 GUI 图标与品牌资源: icon.png / icon.ico / logo-mark.png / logo-wordmark.svg。
# 源图是 artifacts020/gpu-screenshots 下的深蓝底白鲸截图, 输出全部落在仓库内, 可重复运行。
# icon 系列由矢量化轮廓直接渲染(圆角深蓝底 + 白鲸), logo-mark 由源图 alpha 提取, 另外导出预览图供检查。
# 依赖 Windows 上 pwsh 自带的 System.Drawing.Common (GDI+)。

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $root "artifacts020\gpu-screenshots\94c5de49903710d051d8e566ad6d8089.png"
$assetsDir = Join-Path $root "src\Dsh.Gui\Assets"
$previewPath = Join-Path $root "artifacts020\gpu-screenshots\logo-mark-preview.png"
$iconPreviewPath = Join-Path $root "artifacts020\gpu-screenshots\icon-256-preview.png"
$markSize = 128
$simplifyTolerance = 0.6
$backgroundCut = 0.04
$iconSize = 256
$iconCornerRadius = 56
$iconBackground = [System.Drawing.Color]::FromArgb(255, 0x16, 0x30, 0x4F)

function Get-PixelBuffer {
    param([System.Drawing.Bitmap]$Bitmap)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = [Math]::Abs($data.Stride)
        $bytes = [byte[]]::new($stride * $Bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    } finally {
        $Bitmap.UnlockBits($data)
    }
    return @{ Bytes = $bytes; Stride = $stride }
}

function Set-PixelBuffer {
    param([System.Drawing.Bitmap]$Bitmap, [byte[]]$Bytes)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        [System.Runtime.InteropServices.Marshal]::Copy($Bytes, 0, $data.Scan0, $Bytes.Length)
    } finally {
        $Bitmap.UnlockBits($data)
    }
}

# 等比缩放到 size x size 的正方形画布; Fill 的 alpha 为 0 时画布保持透明。
function New-ScaledCanvas {
    param([System.Drawing.Image]$Image, [int]$Size, [System.Drawing.Color]$Fill)
    $canvas = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        if ($Fill.A -gt 0) { $graphics.Clear($Fill) }
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = [Math]::Min($Size / $Image.Width, $Size / $Image.Height)
        $w = [int][Math]::Round($Image.Width * $scale)
        $h = [int][Math]::Round($Image.Height * $scale)
        $x = [int][Math]::Floor(($Size - $w) / 2)
        $y = [int][Math]::Floor(($Size - $h) / 2)
        $graphics.DrawImage($Image, [System.Drawing.Rectangle]::new($x, $y, $w, $h))
    } finally {
        $graphics.Dispose()
    }
    return $canvas
}

# 圆角矩形路径, 尺寸与圆角都用目标画布像素。
function New-RoundedRectanglePath {
    param([single]$Size, [single]$Radius)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2.0
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# 用矢量化轮廓渲染图标: 圆角深蓝底 + 白色鲸鱼(Alternate 填充等价 evenodd), 轮廓按 markSize 的 viewBox 等比放大。
function New-VectorIcon {
    param([System.Collections.Generic.List[System.Drawing.Point[]]]$Rings, [int]$Size, [System.Drawing.Color]$Background)
    $canvas = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $whaleScale = $Size / $markSize
        $cornerRadius = $iconCornerRadius * $Size / $iconSize
        $backgroundPath = New-RoundedRectanglePath $Size $cornerRadius
        $backgroundBrush = [System.Drawing.SolidBrush]::new($Background)
        try { $graphics.FillPath($backgroundBrush, $backgroundPath) } finally { $backgroundBrush.Dispose() }
        $backgroundPath.Dispose()
        $whalePath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $whalePath.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
            foreach ($ring in $Rings) {
                $points = [System.Drawing.PointF[]]::new($ring.Length)
                for ($k = 0; $k -lt $ring.Length; $k++) {
                    $points[$k] = [System.Drawing.PointF]::new([single]($ring[$k].X * $whaleScale), [single]($ring[$k].Y * $whaleScale))
                }
                $whalePath.AddPolygon($points)
            }
            $whaleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
            try { $graphics.FillPath($whaleBrush, $whalePath) } finally { $whaleBrush.Dispose() }
        } finally {
            $whalePath.Dispose()
        }
    } finally {
        $graphics.Dispose()
    }
    return $canvas
}

# 连通域标记: 返回每个区域包含的像素下标数组, 按扫描顺序确定起始区域。
function Get-ConnectedRegions {
    param([bool[]]$Mask, [int]$Width, [int]$Height, [bool]$Diagonal)
    $offsets = if ($Diagonal) { @(@(1, 0), @(-1, 0), @(0, 1), @(0, -1), @(1, 1), @(1, -1), @(-1, 1), @(-1, -1)) } else { @(@(1, 0), @(-1, 0), @(0, 1), @(0, -1)) }
    $visited = [bool[]]::new($Mask.Length)
    $regions = [System.Collections.Generic.List[int[]]]::new()
    for ($start = 0; $start -lt $Mask.Length; $start++) {
        if (-not $Mask[$start] -or $visited[$start]) { continue }
        $stack = [System.Collections.Generic.Stack[int]]::new()
        $stack.Push($start)
        $visited[$start] = $true
        $cells = [System.Collections.Generic.List[int]]::new()
        while ($stack.Count -gt 0) {
            $cell = $stack.Pop()
            $cells.Add($cell)
            $cx = $cell % $Width
            $cy = [int][Math]::Floor($cell / $Width)
            foreach ($offset in $offsets) {
                $nx = $cx + $offset[0]
                $ny = $cy + $offset[1]
                if ($nx -lt 0 -or $ny -lt 0 -or $nx -ge $Width -or $ny -ge $Height) { continue }
                $next = $ny * $Width + $nx
                if ($Mask[$next] -and -not $visited[$next]) {
                    $visited[$next] = $true
                    $stack.Push($next)
                }
            }
        }
        $regions.Add($cells.ToArray())
    }
    return ,$regions
}

# Moore 邻域边界追踪(带回溯 + Jacob 停止准则), 输入区域的首像素需为扫描序最左上像素。
function Trace-MooreRegion {
    param([bool[]]$Mask, [int]$Width, [int]$Height, [int]$Start)
    $dx = @(1, 1, 0, -1, -1, -1, 0, 1)
    $dy = @(0, 1, 1, 1, 0, -1, -1, -1)
    $startX = $Start % $Width
    $startY = [int][Math]::Floor($Start / $Width)
    $contour = [System.Collections.Generic.List[System.Drawing.Point]]::new()
    $contour.Add([System.Drawing.Point]::new($startX, $startY))
    $x = $startX
    $y = $startY
    $backX = $startX - 1
    $backY = $startY
    $firstBackX = $backX
    $firstBackY = $backY
    $steps = 0
    $maxSteps = $Width * $Height * 32
    while ($true) {
        $steps++
        if ($steps -gt $maxSteps) { throw "边界追踪超过步数上限, 掩码可能损坏" }
        $direction = -1
        for ($k = 0; $k -lt 8; $k++) {
            if ($x + $dx[$k] -eq $backX -and $y + $dy[$k] -eq $backY) { $direction = $k; break }
        }
        if ($direction -lt 0) { throw "边界追踪失败: 回溯点不在当前像素邻域内" }
        $nextX = -1
        $nextY = -1
        for ($k = 1; $k -le 8; $k++) {
            $probe = ($direction + $k) % 8
            $px = $x + $dx[$probe]
            $py = $y + $dy[$probe]
            if ($px -ge 0 -and $py -ge 0 -and $px -lt $Width -and $py -lt $Height -and $Mask[$py * $Width + $px]) {
                $nextX = $px
                $nextY = $py
                $previous = ($direction + $k - 1) % 8
                $backX = $x + $dx[$previous]
                $backY = $y + $dy[$previous]
                break
            }
        }
        if ($nextX -lt 0) { throw "边界追踪失败: 当前像素没有可前进的邻域" }
        if ($nextX -eq $startX -and $nextY -eq $startY -and $backX -eq $firstBackX -and $backY -eq $firstBackY) { break }
        $contour.Add([System.Drawing.Point]::new($nextX, $nextY))
        $x = $nextX
        $y = $nextY
    }
    return ,$contour
}

function Get-PointSegmentDistance {
    param([System.Drawing.Point]$Point, [System.Drawing.Point]$A, [System.Drawing.Point]$B)
    $abX = $B.X - $A.X
    $abY = $B.Y - $A.Y
    $length = [Math]::Sqrt($abX * $abX + $abY * $abY)
    if ($length -eq 0) {
        return [Math]::Sqrt(($Point.X - $A.X) * ($Point.X - $A.X) + ($Point.Y - $A.Y) * ($Point.Y - $A.Y))
    }
    return [Math]::Abs($abX * ($A.Y - $Point.Y) - ($A.X - $Point.X) * $abY) / $length
}

function Invoke-DouglasPeucker {
    param([System.Collections.Generic.List[System.Drawing.Point]]$Points, [double]$Tolerance)
    $count = $Points.Count
    if ($count -lt 3) { return ,$Points }
    $keep = [bool[]]::new($count)
    $keep[0] = $true
    $keep[$count - 1] = $true
    $stack = [System.Collections.Generic.Stack[int[]]]::new()
    $stack.Push([int[]]@(0, ($count - 1)))
    while ($stack.Count -gt 0) {
        $range = $stack.Pop()
        $first = $range[0]
        $last = $range[1]
        $maxDistance = -1.0
        $maxIndex = -1
        for ($k = $first + 1; $k -lt $last; $k++) {
            $distance = Get-PointSegmentDistance $Points[$k] $Points[$first] $Points[$last]
            if ($distance -gt $maxDistance) { $maxDistance = $distance; $maxIndex = $k }
        }
        if ($maxIndex -ge 0 -and $maxDistance -gt $Tolerance) {
            $keep[$maxIndex] = $true
            $stack.Push([int[]]@($first, $maxIndex))
            $stack.Push([int[]]@($maxIndex, $last))
        }
    }
    $result = [System.Collections.Generic.List[System.Drawing.Point]]::new()
    for ($k = 0; $k -lt $count; $k++) {
        if ($keep[$k]) { $result.Add($Points[$k]) }
    }
    return ,$result
}

# 闭环简化: 以距起点最远的顶点把闭环切成两条折线分别简化, 避免闭合接缝处无法压缩。
function Simplify-ClosedPolygon {
    param([System.Collections.Generic.List[System.Drawing.Point]]$Points, [double]$Tolerance)
    $count = $Points.Count
    if ($count -lt 4) { return ,$Points }
    $origin = $Points[0]
    $far = 1
    $farDistance = -1.0
    for ($k = 1; $k -lt $count; $k++) {
        $dx = $Points[$k].X - $origin.X
        $dy = $Points[$k].Y - $origin.Y
        $distance = $dx * $dx + $dy * $dy
        if ($distance -gt $farDistance) { $farDistance = $distance; $far = $k }
    }
    $first = [System.Collections.Generic.List[System.Drawing.Point]]::new()
    for ($k = 0; $k -le $far; $k++) { $first.Add($Points[$k]) }
    $second = [System.Collections.Generic.List[System.Drawing.Point]]::new()
    for ($k = $far; $k -lt $count; $k++) { $second.Add($Points[$k]) }
    $second.Add($origin)
    $simplifiedFirst = Invoke-DouglasPeucker $first $Tolerance
    $simplifiedSecond = Invoke-DouglasPeucker $second $Tolerance
    $result = [System.Collections.Generic.List[System.Drawing.Point]]::new()
    for ($k = 0; $k -lt $simplifiedFirst.Count - 1; $k++) { $result.Add($simplifiedFirst[$k]) }
    for ($k = 0; $k -lt $simplifiedSecond.Count - 1; $k++) { $result.Add($simplifiedSecond[$k]) }
    return ,$result
}

# 多尺寸 ICO: 每个尺寸都直接用 Graphics 渲染矢量轮廓, 每帧用 PNG 编码, 目录项 16 字节, 宽高为 0 表示 256。
function Write-IcoFile {
    param([System.Collections.Generic.List[System.Drawing.Point[]]]$Rings, [int[]]$Sizes, [System.Drawing.Color]$Background, [string]$Path)
    $blobs = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $Sizes) {
        $frame = New-VectorIcon $Rings $size $Background
        $stream = [System.IO.MemoryStream]::new()
        try {
            $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $blobs.Add($stream.ToArray())
        } finally {
            $stream.Dispose()
            $frame.Dispose()
        }
    }
    $file = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Sizes.Count)
        $offset = 6 + 16 * $Sizes.Count
        for ($k = 0; $k -lt $Sizes.Count; $k++) {
            $size = $Sizes[$k]
            $blob = $blobs[$k]
            $dimension = [byte]0
            if ($size -lt 256) { $dimension = [byte]$size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$blob.Length)
            $writer.Write([uint32]$offset)
            $offset += $blob.Length
        }
        foreach ($blob in $blobs) { $writer.Write($blob) }
    } finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

# 把 SVG 的 d 重新解析成多边形渲染成 PNG, 供人眼检查矢量化效果。
function Export-SvgPreview {
    param([string]$SvgPath, [string]$OutPath, [int]$CanvasSize, [int]$Scale, [System.Drawing.Color]$Background)
    $svg = Get-Content -LiteralPath $SvgPath -Raw
    $match = [regex]::Match($svg, 'd="([^"]*)"')
    if (-not $match.Success) { throw "SVG 缺少 path 的 d 属性: $SvgPath" }
    $canvas = [System.Drawing.Bitmap]::new($CanvasSize * $Scale, $CanvasSize * $Scale, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.Clear($Background)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
        $polygon = [System.Collections.Generic.List[System.Drawing.PointF]]::new()
        foreach ($command in [regex]::Matches($match.Groups[1].Value, '([ML])(-?\d+) (-?\d+)')) {
            if ($command.Groups[1].Value -eq 'M' -and $polygon.Count -gt 2) {
                $path.AddPolygon($polygon.ToArray())
                $polygon = [System.Collections.Generic.List[System.Drawing.PointF]]::new()
            }
            $x = [single]([int]$command.Groups[2].Value * $Scale)
            $y = [single]([int]$command.Groups[3].Value * $Scale)
            $polygon.Add([System.Drawing.PointF]::new($x, $y))
        }
        if ($polygon.Count -gt 2) { $path.AddPolygon($polygon.ToArray()) }
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        try { $graphics.FillPath($brush, $path) } finally { $brush.Dispose() }
        $path.Dispose()
    } finally {
        $graphics.Dispose()
    }
    try {
        $canvas.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $canvas.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $sourcePath)) { throw "缺少图标源图: $sourcePath" }
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

$source = [System.Drawing.Bitmap]::new($sourcePath)
try {
    # 取边框平均色作为源图底色参考, 供 logo-mark 的 alpha 提取与预览背景使用。
    $sourceBuffer = Get-PixelBuffer $source
    $sumR = 0
    $sumG = 0
    $sumB = 0
    $borderCount = 0
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            if ($x -ge 2 -and $y -ge 2 -and $x -lt $source.Width - 2 -and $y -lt $source.Height - 2) { continue }
            $i = $y * $sourceBuffer.Stride + $x * 4
            if ($sourceBuffer.Bytes[$i + 3] -lt 128) { continue }
            $sumB += $sourceBuffer.Bytes[$i]
            $sumG += $sourceBuffer.Bytes[$i + 1]
            $sumR += $sourceBuffer.Bytes[$i + 2]
            $borderCount++
        }
    }
    if ($borderCount -eq 0) { throw "源图边框全部透明, 无法取底色" }
    $bgR = [int][Math]::Round($sumR / $borderCount)
    $bgG = [int][Math]::Round($sumG / $borderCount)
    $bgB = [int][Math]::Round($sumB / $borderCount)
    $bgColor = [System.Drawing.Color]::FromArgb(255, $bgR, $bgG, $bgB)

    # logo-mark.png: 透明背景白鲸, 按像素与底色的距离生成 alpha 渐变, 保留抗锯齿边缘。
    $markPath = Join-Path $assetsDir "logo-mark.png"
    $mark = New-ScaledCanvas $source $markSize ([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $markBuffer = Get-PixelBuffer $mark
    $axisR = 255.0 - $bgR
    $axisG = 255.0 - $bgG
    $axisB = 255.0 - $bgB
    $axisLength2 = $axisR * $axisR + $axisG * $axisG + $axisB * $axisB
    $markBytes = $markBuffer.Bytes
    $mask = [bool[]]::new($markSize * $markSize)
    $foregroundCount = 0
    for ($y = 0; $y -lt $markSize; $y++) {
        for ($x = 0; $x -lt $markSize; $x++) {
            $i = $y * $markBuffer.Stride + $x * 4
            $alpha = 0.0
            if ($markBytes[$i + 3] -gt 0) {
                $b = $markBytes[$i]
                $g = $markBytes[$i + 1]
                $r = $markBytes[$i + 2]
                $alpha = (($r - $bgR) * $axisR + ($g - $bgG) * $axisG + ($b - $bgB) * $axisB) / $axisLength2
                if ($alpha -lt 0.0) { $alpha = 0.0 }
                if ($alpha -gt 1.0) { $alpha = 1.0 }
                # 源图底色带噪, 距底色 backgroundCut 比例以内的像素全透明, 避免留下一层极淡的白雾。
                $alpha = ($alpha - $backgroundCut) / (1.0 - $backgroundCut)
                if ($alpha -lt 0.0) { $alpha = 0.0 }
            }
            $markBytes[$i] = 255
            $markBytes[$i + 1] = 255
            $markBytes[$i + 2] = 255
            $markBytes[$i + 3] = [byte][int][Math]::Round($alpha * 255.0)
            if ($alpha -ge 0.5) {
                $mask[$y * $markSize + $x] = $true
                $foregroundCount++
            }
        }
    }
    Set-PixelBuffer $mark $markBytes
    try { $mark.Save($markPath, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $mark.Dispose() }

    # logo-wordmark.svg: 对白/非白掩码做边界追踪 -> Douglas-Peucker 简化 -> 单条 path。
    $rings = [System.Collections.Generic.List[System.Drawing.Point[]]]::new()
    $rawPointCount = 0
    $simplifiedPointCount = 0
    $backgroundMask = [bool[]]::new($mask.Length)
    for ($i = 0; $i -lt $mask.Length; $i++) { $backgroundMask[$i] = -not $mask[$i] }
    $regions = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($cells in (Get-ConnectedRegions $mask $markSize $markSize $true)) {
        $regions.Add(@{ Cells = $cells; Mask = $mask })
    }
    foreach ($cells in (Get-ConnectedRegions $backgroundMask $markSize $markSize $false)) {
        $touchesBorder = $false
        foreach ($cell in $cells) {
            $cellX = $cell % $markSize
            $cellY = [int][Math]::Floor($cell / $markSize)
            if ($cellX -eq 0 -or $cellY -eq 0 -or $cellX -eq $markSize - 1 -or $cellY -eq $markSize - 1) { $touchesBorder = $true; break }
        }
        if ($touchesBorder) { continue }
        $holeMask = [bool[]]::new($mask.Length)
        foreach ($cell in $cells) { $holeMask[$cell] = $true }
        $regions.Add(@{ Cells = $cells; Mask = $holeMask })
    }
    foreach ($region in $regions) {
        $contour = Trace-MooreRegion $region.Mask $markSize $markSize $region.Cells[0]
        $rawPointCount += $contour.Count
        $simplified = Simplify-ClosedPolygon $contour $simplifyTolerance
        if ($simplified.Count -lt 3) { continue }
        $simplifiedPointCount += $simplified.Count
        $rings.Add($simplified.ToArray())
    }
    $builder = [System.Text.StringBuilder]::new()
    foreach ($ring in $rings) {
        [void]$builder.Append('M').Append($ring[0].X).Append(' ').Append($ring[0].Y)
        for ($k = 1; $k -lt $ring.Length; $k++) {
            [void]$builder.Append('L').Append($ring[$k].X).Append(' ').Append($ring[$k].Y)
        }
        [void]$builder.Append('Z')
    }
    $pathData = $builder.ToString()
    $svgPath = Join-Path $assetsDir "logo-wordmark.svg"
    $svgText = @"
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 $markSize $markSize" width="$markSize" height="$markSize">
  <path d="$pathData" fill="#FFFFFF" fill-rule="evenodd"/>
</svg>
"@
    [System.IO.File]::WriteAllText($svgPath, $svgText, [System.Text.UTF8Encoding]::new($false))

    # icon.png / icon.ico: 直接用矢量轮廓渲染圆角深蓝底 + 白鲸, 每个尺寸单独渲染, 避免位图放大的模糊。
    $iconPath = Join-Path $assetsDir "icon.png"
    $icon = New-VectorIcon $rings $iconSize $iconBackground
    try { $icon.Save($iconPath, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $icon.Dispose() }

    $icoPath = Join-Path $assetsDir "icon.ico"
    Write-IcoFile $rings @(16, 24, 32, 48, 64, 128, 256) $iconBackground $icoPath

    # 图标放大预览: 同一矢量按 2 倍渲染, 用于人眼检查清晰度。
    $iconPreview = New-VectorIcon $rings 512 $iconBackground
    try { $iconPreview.Save($iconPreviewPath, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $iconPreview.Dispose() }

    Export-SvgPreview $svgPath $previewPath $markSize 4 $bgColor
} finally {
    $source.Dispose()
}

foreach ($outputPath in @($iconPath, $icoPath, $markPath, $svgPath, $previewPath, $iconPreviewPath)) {
    $item = Get-Item -LiteralPath $outputPath
    "$($item.FullName)  $($item.Length) bytes"
}
"SVG 子路径 $($rings.Count) 条, 原始轮廓点 $rawPointCount, 简化后 $simplifiedPointCount (容差 $simplifyTolerance, 各子路径点数 $(($rings | ForEach-Object { $_.Length }) -join '/'))"
"logo-mark 前景像素 $foregroundCount / $($markSize * $markSize), 底色 R=$bgR G=$bgG B=$bgB"
