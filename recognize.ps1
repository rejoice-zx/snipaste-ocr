param([Parameter(Mandatory)][string] $LanguageTag)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$ocrType = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$resultType = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
$decoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
$bitmapType = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
$pixelFormat = [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Foundation, ContentType=WindowsRuntime]::Bgra8
$alphaMode = [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Foundation, ContentType=WindowsRuntime]::Premultiplied
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -like 'IAsyncOperation*'
} | Select-Object -First 1

function Get-WinRtResult($operation, [type] $resultType) {
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.GetAwaiter().GetResult()
}

function Read-Lines($bitmap, $language) {
    $engine = $ocrType::TryCreateFromLanguage($language)
    $result = Get-WinRtResult ($engine.RecognizeAsync($bitmap)) $resultType
    foreach ($line in $result.Lines) {
        $box = ($line.Words | Select-Object -First 1).BoundingRect
        [pscustomobject]@{ Text = $line.Text; Left = $box.X; CenterY = $box.Y + $box.Height / 2; Height = $box.Height }
    }
}

$png = [System.IO.MemoryStream]::new()
$bitmap = $null
try {
    $languages = $ocrType::AvailableRecognizerLanguages | Where-Object {
        $_.LanguageTag -eq $LanguageTag -or ($LanguageTag -eq 'auto' -and $_.LanguageTag -in 'en-US', 'zh-Hans-CN')
    }
    if (-not $languages) { throw "Windows OCR language is missing: $LanguageTag" }
    [Console]::OpenStandardInput().CopyTo($png)
    $png.Position = 0
    $stream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($png)
    $decoder = Get-WinRtResult ($decoderType::CreateAsync($stream)) $decoderType
    $longestSide = [Math]::Max($decoder.PixelWidth, $decoder.PixelHeight)
    $desiredScale = if ($longestSide -lt 900) { 2.0 } else { 1.0 }
    $scale = [Math]::Min($desiredScale, [double]$ocrType::MaxImageDimension / $longestSide)
    if ($scale -ne 1) {
        $transform = [Windows.Graphics.Imaging.BitmapTransform, Windows.Foundation, ContentType=WindowsRuntime]::new()
        $transform.ScaledWidth = [uint32][Math]::Max(1, [Math]::Floor($decoder.PixelWidth * $scale))
        $transform.ScaledHeight = [uint32][Math]::Max(1, [Math]::Floor($decoder.PixelHeight * $scale))
        $transform.InterpolationMode = [Windows.Graphics.Imaging.BitmapInterpolationMode, Windows.Foundation, ContentType=WindowsRuntime]::Cubic
        $orientation = [Windows.Graphics.Imaging.ExifOrientationMode, Windows.Foundation, ContentType=WindowsRuntime]::IgnoreExifOrientation
        $color = [Windows.Graphics.Imaging.ColorManagementMode, Windows.Foundation, ContentType=WindowsRuntime]::DoNotColorManage
        $bitmap = Get-WinRtResult ($decoder.GetSoftwareBitmapAsync($pixelFormat, $alphaMode, $transform, $orientation, $color)) $bitmapType
    } else {
        $bitmap = Get-WinRtResult ($decoder.GetSoftwareBitmapAsync($pixelFormat, $alphaMode)) $bitmapType
    }
    $results = @{}
    foreach ($language in $languages) { $results[$language.LanguageTag] = @(Read-Lines $bitmap $language) }
    $lines = $results[$LanguageTag]
    if ($LanguageTag -eq 'auto') {
        $english = $results['en-US']
        $chinese = $results['zh-Hans-CN']
        $lines = $english
        if (-not $english -or ((($chinese.Text -join '') -replace '[ \t]', '') -match '[\u3400-\u9fff]{2}')) {
            $lines = foreach ($line in $chinese) {
                $candidate = $english | Where-Object {
                    [Math]::Abs($_.CenterY - $line.CenterY) -lt $line.Height / 2 -and
                    [Math]::Abs($_.Left - $line.Left) -lt $line.Height
                } | Select-Object -First 1
                if ($candidate -and $line.Text -notmatch '[\u3400-\u9fff]') { $candidate } else { $line }
            }
        }
    }
    $text = ($lines | ForEach-Object Text) -join [Environment]::NewLine
    [Console]::Write(($text -replace '(?<=[\u3400-\u9fff])[ \t]+(?=[\u3400-\u9fff])', ''))
} catch {
    [Console]::Write($_.Exception.Message)
    exit 1
} finally {
    if ($bitmap) { $bitmap.Dispose() }
    $png.Dispose()
}
