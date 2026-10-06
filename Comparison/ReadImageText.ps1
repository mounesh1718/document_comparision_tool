$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
    $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    $null = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
    $asyncBridge = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetGenericArguments().Count -eq 1
    } | Select-Object -First 1
    function Await-Result($operation, [Type]$type) {
        $task = $asyncBridge.MakeGenericMethod($type).Invoke($null, @($operation))
        $task.GetAwaiter().GetResult()
    }
    $bytes = [Convert]::FromBase64String([Console]::ReadLine())
    $memory = New-Object System.IO.MemoryStream(,$bytes)
    $stream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
    $decoder = Await-Result ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await-Result ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -eq $engine) {
        [Console]::WriteLine('{"Error":"No Windows OCR language is installed. Review the image crops."}')
        exit 0
    }
    $result = Await-Result ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $words = @($result.Lines | ForEach-Object { $_.Words } | ForEach-Object {
        @{ Text = $_.Text; X = $_.BoundingRect.X; Y = $_.BoundingRect.Y; Width = $_.BoundingRect.Width; Height = $_.BoundingRect.Height }
    })
    [Console]::WriteLine((@{ Words = $words } | ConvertTo-Json -Depth 5 -Compress))
    $bitmap.Dispose()
    $stream.Dispose()
    $memory.Dispose()
} catch {
    [Console]::WriteLine('{"Error":"Windows could not recognize this image. Review the image crops."}')
}
