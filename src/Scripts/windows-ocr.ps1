param([Parameter(Mandatory=$true)][string]$ImagePath, [string]$Language = "en-US")
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 + built-in Windows.Media.Ocr (no online OCR service).
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Foundation, ContentType = WindowsRuntime]

function Await-WinRT($operation, [Type]$resultType) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } |
        Select-Object -First 1
    $task = $method.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.Wait()
    return $task.Result
}

try {
    $file = Await-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync([IO.Path]::GetFullPath($ImagePath))) ([Windows.Storage.StorageFile])
    $stream = Await-WinRT ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await-WinRT ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($Language)))
    if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
    if ($null -eq $engine) { throw 'Windows OCR is unavailable. Install English OCR under Windows language features.' }
    $result = Await-WinRT ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines = @()
    foreach ($line in $result.Lines) {
        $words = @()
        $left = [double]::PositiveInfinity; $top = [double]::PositiveInfinity
        $right = 0.0; $bottom = 0.0
        foreach ($word in $line.Words) {
            $r = $word.BoundingRect
            $words += [ordered]@{text=$word.Text;box=[ordered]@{x=[double]$r.X;y=[double]$r.Y;width=[double]$r.Width;height=[double]$r.Height}}
            $left = [math]::Min($left, [double]$r.X); $top = [math]::Min($top, [double]$r.Y)
            $right = [math]::Max($right, [double]($r.X+$r.Width)); $bottom = [math]::Max($bottom, [double]($r.Y+$r.Height))
        }
        if ($words.Count -gt 0) {
            $lines += [ordered]@{text=$line.Text;box=[ordered]@{x=$left;y=$top;width=($right-$left);height=($bottom-$top)};words=$words}
        }
    }
    [ordered]@{text=$result.Text;language=$engine.RecognizerLanguage.LanguageTag;lines=@($lines)} |
      ConvertTo-Json -Depth 7 -Compress | Write-Output
    try { $bitmap.Dispose(); $stream.Dispose() } catch { }
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
