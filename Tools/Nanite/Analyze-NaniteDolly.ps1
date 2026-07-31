param(
    [Parameter(Mandatory = $true)] [string] $NaniteCapture,
    [Parameter(Mandatory = $true)] [string] $RasterCapture,
    [string] $OutputCsv = "",
    [int] $ChannelThreshold = 8
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class DollyImageComparison
{
    public long AbsoluteSum;
    public long ThresholdChannels;
    public long ThresholdPixels;
    public long ChangedPixels;
    public long TemporalErrorSum;
    public int MaxDifference;
    public int Width;
    public int Height;
    public sbyte[] Error;
}

public static class DollyImageAnalyzer
{
    static byte[] ReadBgra32(string path, out int width, out int height)
    {
        using (var source = new Bitmap(path))
        using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        {
            width = bitmap.Width;
            height = bitmap.Height;
            using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(source, 0, 0);
            var rectangle = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = width * 4;
                var packed = new byte[rowBytes * height];
                var raw = new byte[Math.Abs(data.Stride) * height];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                for (int y = 0; y < height; ++y)
                {
                    int sourceRow = data.Stride >= 0 ? y * data.Stride : (height - 1 - y) * -data.Stride;
                    Buffer.BlockCopy(raw, sourceRow, packed, y * rowBytes, rowBytes);
                }
                return packed;
            }
            finally { bitmap.UnlockBits(data); }
        }
    }

    public static DollyImageComparison Compare(string nanitePath, string rasterPath, int threshold, sbyte[] previousError)
    {
        int width, height, rasterWidth, rasterHeight;
        byte[] nanite = ReadBgra32(nanitePath, out width, out height);
        byte[] raster = ReadBgra32(rasterPath, out rasterWidth, out rasterHeight);
        if (width != rasterWidth || height != rasterHeight) throw new InvalidOperationException("Image dimensions differ.");

        var result = new DollyImageComparison { Width = width, Height = height, Error = new sbyte[width * height * 3] };
        int errorIndex = 0;
        for (int pixel = 0; pixel < width * height; ++pixel)
        {
            bool pixelOverThreshold = false;
            bool pixelChanged = false;
            int byteIndex = pixel * 4;
            for (int channel = 0; channel < 3; ++channel)
            {
                int difference = nanite[byteIndex + channel] - raster[byteIndex + channel];
                int absolute = Math.Abs(difference);
                result.AbsoluteSum += absolute;
                result.MaxDifference = Math.Max(result.MaxDifference, absolute);
                pixelChanged |= absolute != 0;
                if (absolute > threshold) { ++result.ThresholdChannels; pixelOverThreshold = true; }
                sbyte error = (sbyte)Math.Max(-128, Math.Min(127, difference));
                result.Error[errorIndex] = error;
                if (previousError != null) result.TemporalErrorSum += Math.Abs(error - previousError[errorIndex]);
                ++errorIndex;
            }
            if (pixelChanged) ++result.ChangedPixels;
            if (pixelOverThreshold) ++result.ThresholdPixels;
        }
        return result;
    }
}
'@

function Get-FrameNumber([string] $Name) {
    if ($Name -notmatch '^frame-(\d+)\.png$') { return $null }
    return [int] $Matches[1]
}

$telemetry = @{}
$telemetryPath = Join-Path $NaniteCapture "nanite-telemetry.csv"
if (Test-Path -LiteralPath $telemetryPath) {
    Import-Csv -LiteralPath $telemetryPath | ForEach-Object { $telemetry[[int] $_.smokeFrame] = $_ }
}

$naniteFrames = @{}
Get-ChildItem -LiteralPath $NaniteCapture -Filter "frame-*.png" | ForEach-Object {
    $number = Get-FrameNumber $_.Name
    if ($null -ne $number) { $naniteFrames[$number] = $_.FullName }
}
$rasterFrames = @{}
Get-ChildItem -LiteralPath $RasterCapture -Filter "frame-*.png" | ForEach-Object {
    $number = Get-FrameNumber $_.Name
    if ($null -ne $number) { $rasterFrames[$number] = $_.FullName }
}

$frameNumbers = @($naniteFrames.Keys | Where-Object { $rasterFrames.ContainsKey($_) } | Sort-Object)
if ($frameNumbers.Count -eq 0) { throw "No matching frame-####.png pairs were found." }

$rows = New-Object System.Collections.Generic.List[object]
$previousError = $null
$previousFrame = $null
foreach ($frame in $frameNumbers) {
    $comparison = [DollyImageAnalyzer]::Compare($naniteFrames[$frame], $rasterFrames[$frame], $ChannelThreshold, $previousError)
    $channelCount = [double] ($comparison.Width * $comparison.Height * 3)
    $pixelCount = [double] ($comparison.Width * $comparison.Height)
    $sample = if ($telemetry.ContainsKey($frame)) { $telemetry[$frame] } else { $null }
    $rows.Add([pscustomobject]@{
        frame = $frame
        previousFrame = $previousFrame
        surfaceDistanceR = if ($null -ne $sample) { [double] $sample.surfaceDistanceR } else { [double]::NaN }
        cameraClusters = if ($null -ne $sample) { [int] $sample.cameraClusters } else { -1 }
        cameraTriangles = if ($null -ne $sample) { [int] $sample.cameraTriangles } else { -1 }
        maeRgb = $comparison.AbsoluteSum / $channelCount
        changedPixelPercent = 100.0 * $comparison.ChangedPixels / $pixelCount
        thresholdChannelPercent = 100.0 * $comparison.ThresholdChannels / $channelCount
        thresholdPixelPercent = 100.0 * $comparison.ThresholdPixels / $pixelCount
        maxChannelDifference = $comparison.MaxDifference
        temporalErrorMae = if ($null -ne $previousError) { $comparison.TemporalErrorSum / $channelCount } else { 0.0 }
    })
    $previousError = $comparison.Error
    $previousFrame = $frame
}

if ([string]::IsNullOrWhiteSpace($OutputCsv)) { $OutputCsv = Join-Path $NaniteCapture "dolly-comparison.csv" }
$rows | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation

Write-Host "Compared $($rows.Count) matched frames. Output: $OutputCsv"
Write-Host "Worst Nanite/Raster MAE:"
$rows | Sort-Object maeRgb -Descending | Select-Object -First 10 frame,surfaceDistanceR,cameraClusters,cameraTriangles,maeRgb,thresholdPixelPercent,maxChannelDifference | Format-Table -AutoSize
Write-Host "Largest change in the Nanite/Raster error field between captured frames:"
$rows | Sort-Object temporalErrorMae -Descending | Select-Object -First 10 frame,previousFrame,surfaceDistanceR,cameraClusters,cameraTriangles,temporalErrorMae,maeRgb | Format-Table -AutoSize
