# iTu 文字提取脚本（Windows 内置 OCR，无需第三方依赖）
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ocr.ps1 -Path <图片路径>
#       输出：识别到的整段文本
#   powershell -NoProfile -ExecutionPolicy Bypass -File ocr.ps1 -Path <图片路径> -Lines
#       输出：JSON 数组，每项为该行的文本与包围盒 { t, x, y, w, h }
#             （供「翻译叠加」按原位置回填译文）
#   iTu.exe 内嵌本脚本，运行时用 -EncodedCommand 执行，参数经环境变量 ITU_OCR_PATH /
#       ITU_OCR_LINES 传入（脚本不落盘，发布目录只需一个 exe）。

param(
    [string]$Path,
    [switch]$Lines
)

# 内嵌执行（-EncodedCommand）时没有命令行参数，从环境变量补齐。
if ([string]::IsNullOrEmpty($Path)) { $Path = $env:ITU_OCR_PATH }
if ($env:ITU_OCR_LINES -eq '1') { $Lines = $true }
if ([string]::IsNullOrEmpty($Path)) {
    [Console]::Error.WriteLine('NO_IMAGE_PATH')
    exit 3
}

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

    $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Foundation, ContentType = WindowsRuntime]

    # 取出 AsTask<T> 的泛型方法，用于等待 WinRT 异步操作
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

    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -eq $engine) {
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage(
            [Windows.Globalization.Language]::new('zh-Hans'))
    }
    if ($null -eq $engine) {
        # 不能用 Write-Error：$ErrorActionPreference='Stop' 下它会抛异常，
        # 直接进入外层 catch → exit 1，导致 C# 端针对 exit 2 的
        # "未安装 OCR 语言包"友好提示永远不可达。这里直接写 stderr 并以 2 退出。
        [Console]::Error.WriteLine('NO_OCR_ENGINE')
        exit 2
    }

    $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    if ($Lines) {
        # 逐行输出文本 + 包围盒（Word 的 BoundingRect 并集即整行范围）
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
        # PowerShell 5.1 的 ConvertTo-Json 不支持 -AsArray，手工拼数组保证格式稳定
        $items = @($rows | ForEach-Object { $_ | ConvertTo-Json -Compress })
        Write-Output ('[' + ($items -join ',') + ']')
        exit 0
    }

    Write-Output $result.Text
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
