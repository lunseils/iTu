# iTu OCR script (Windows built-in OCR, no third-party dependency)
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ocr.ps1 -Path <image>
#       Output: recognized plain text
#   powershell -NoProfile -ExecutionPolicy Bypass -File ocr.ps1 -Path <image> -Lines
#       Output: JSON array of { t, x, y, w, h } for each recognized line
#   iTu.exe embeds this script and runs it via -EncodedCommand. Arguments are
#   passed through ITU_OCR_PATH / ITU_OCR_LINES environment variables.
#
# NOTE: Keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less UTF-8
# as ANSI (GBK on Chinese systems); multibyte comments can swallow following
# newlines and silently comment out code (this caused a real OCR failure).

param(
    [string]$Path,
    [switch]$Lines
)

# EncodedCommand mode has no command-line arguments.
if ([string]::IsNullOrEmpty($Path)) { $Path = $env:ITU_OCR_PATH }
if ($env:ITU_OCR_LINES -eq '1') { $Lines = $true }
if ([string]::IsNullOrEmpty($Path)) {
    [Console]::Error.WriteLine('NO_IMAGE_PATH')
    exit 3
}

$ErrorActionPreference = 'Stop'

try {
    # WinRT StorageFile.GetFileFromPathAsync requires native backslash paths.
    $ImagePath = [System.IO.Path]::GetFullPath($Path).Replace([char]47, [char]92)

    Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

    $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Foundation, ContentType = WindowsRuntime]
    # Without this projection, [Windows.Globalization.Language] throws
    # "Unable to find type" and the whole recognition process fails.
    $null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

    # Generic AsTask<T> used to await WinRT async operations.
    $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]

    function Await($Operation, $ResultType) {
        $task = $asTaskGeneric.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
        $task.Wait(-1) | Out-Null
        $task.Result
    }

    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

    # Prefer Simplified Chinese engine; user profile language may be English,
    # which badly fragments Chinese text. Fall back silently when unavailable.
    $engine = $null
    try {
        $lang = [Windows.Globalization.Language]::new('zh-Hans')
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
    } catch {
        $engine = $null
    }
    if ($null -eq $engine) {
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    }
    if ($null -eq $engine) {
        # Do NOT use Write-Error: under ErrorActionPreference=Stop it throws and
        # turns into exit 1, making the C# exit-code-2 friendly hint unreachable.
        [Console]::Error.WriteLine('NO_OCR_ENGINE')
        exit 2
    }

    $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    if ($Lines) {
        $rows = New-Object System.Collections.ArrayList
        foreach ($line in $result.Lines) {
            $text = $line.Text
            if ([string]::IsNullOrWhiteSpace($text)) { continue }

            $minX = [double]::MaxValue; $minY = [double]::MaxValue
            $maxX = [double]::MinValue; $maxY = [double]::MinValue
            foreach ($word in $line.Words) {
                $r = $word.BoundingRect
                if ($r.X -lt $minX) { $minX = $r.X }
                if ($r.Y -lt $minY) { $minY = $r.Y }
                if (($r.X + $r.Width) -gt $maxX) { $maxX = $r.X + $r.Width }
                if (($r.Y + $r.Height) -gt $maxY) { $maxY = $r.Y + $r.Height }
            }
            if ($minX -eq [double]::MaxValue) { continue }

            $null = $rows.Add([pscustomobject]@{
                t = $text
                x = [int][math]::Round($minX)
                y = [int][math]::Round($minY)
                w = [int][math]::Round($maxX - $minX)
                h = [int][math]::Round($maxY - $minY)
            })
        }
        # ConvertTo-Json -AsArray is not available in PowerShell 5.1.
        $items = @($rows | ForEach-Object { $_ | ConvertTo-Json -Compress })
        Write-Output ('[' + ($items -join ',') + ']')
        exit 0
    }

    Write-Output $result.Text
    exit 0
}
catch {
    $msg = $_.Exception.Message
    if ($_.Exception.InnerException) { $msg += ' | ' + $_.Exception.InnerException.Message }
    [Console]::Error.WriteLine('OCR_ERROR: ' + $msg)
    exit 1
}
