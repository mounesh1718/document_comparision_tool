using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace DocumentChecker.Engine.Comparison;

public sealed record ImageTextWord(string Text, ImageDifferenceRegion Bounds);
public sealed record ImageTextResult(IReadOnlyList<ImageTextWord> Words, string? Error = null);

public interface IImageTextReader
{
    ImageTextResult Read(byte[] imageData);
}

/// <summary>The text recognition built into this operating system: Windows OCR on Windows, Apple Vision on macOS.</summary>
public sealed class SystemImageTextReader : IImageTextReader
{
    private readonly IImageTextReader _reader = OperatingSystem.IsWindows() ? new WindowsImageTextReader()
        : OperatingSystem.IsMacOS() ? new MacImageTextReader()
        : new UnavailableImageTextReader();

    public ImageTextResult Read(byte[] imageData) => _reader.Read(imageData);

    private sealed class UnavailableImageTextReader : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) =>
            new([], "Image text recognition needs Windows or macOS. Review the image crops.");
    }
}

/// <summary>Runs the Windows OCR runtime locally through its Windows PowerShell host.</summary>
public sealed class WindowsImageTextReader : LocalImageTextReader
{
    protected override bool IsAvailable => OperatingSystem.IsWindows();
    protected override bool ReturnsNormalizedBoxes => false;

    protected override ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(ReadScript("ReadImageText.ps1"))));
        return start;
    }

    protected override void WriteImage(Process process, byte[] png) =>
        process.StandardInput.WriteLine(Convert.ToBase64String(png));
}

/// <summary>
/// Runs Apple's Vision text recognition locally through macOS's built-in JavaScript for Automation (osascript),
/// so nothing has to be installed. Vision reports word boxes normalized to the image, origin bottom-left.
/// </summary>
public sealed class MacImageTextReader : LocalImageTextReader
{
    protected override bool IsAvailable => OperatingSystem.IsMacOS();
    protected override bool ReturnsNormalizedBoxes => true;

    protected override ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo { FileName = "/usr/bin/osascript" };
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add("JavaScript");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(ReadScript("ReadImageTextMac.js"));
        return start;
    }

    protected override void WriteImage(Process process, byte[] png)
    {
        process.StandardInput.BaseStream.Write(png);
        process.StandardInput.BaseStream.Flush();
    }
}

/// <summary>
/// Shared work for the operating-system OCR engines: the image is enlarged for small text, passed to a local
/// helper process, and the recognized words are returned in the original image's pixel coordinates.
/// Results are cached by image content; nothing leaves the machine.
/// </summary>
public abstract class LocalImageTextReader : IImageTextReader
{
    private readonly Dictionary<string, ImageTextResult> _cache = new();

    protected abstract bool IsAvailable { get; }

    /// <summary>True when the engine reports boxes as 0..1 fractions with the origin at the bottom-left (Vision).</summary>
    protected abstract bool ReturnsNormalizedBoxes { get; }

    protected abstract ProcessStartInfo CreateStartInfo();

    protected abstract void WriteImage(Process process, byte[] png);

    public ImageTextResult Read(byte[] imageData)
    {
        if (!IsAvailable)
            return new([], "Image text recognition is not available on this system. Review the image crops.");
        var key = Convert.ToHexString(SHA256.HashData(imageData));
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        try
        {
            using var image = Image.Load(imageData);
            var originalWidth = image.Width;
            var originalHeight = image.Height;
            var scale = Math.Min(2d, 2400d / Math.Max(image.Width, image.Height));
            image.Mutate(context => context.Resize(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))));
            using var buffer = new MemoryStream();
            image.SaveAsPng(buffer);

            var start = CreateStartInfo();
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            WriteImage(process, buffer.ToArray());
            process.StandardInput.Close();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                return new([], "Image text recognition timed out. Review the image crops.");
            }
            if (process.ExitCode != 0)
                return new([], "Image text recognition is unavailable. Review the image crops.");
            using var json = JsonDocument.Parse(output.GetAwaiter().GetResult());
            if (json.RootElement.TryGetProperty("Error", out var issue))
                return new([], issue.GetString());

            var words = json.RootElement.GetProperty("Words").EnumerateArray()
                .Select(word => new ImageTextWord(word.GetProperty("Text").GetString()!, ToPixels(
                    word.GetProperty("X").GetDouble(), word.GetProperty("Y").GetDouble(),
                    word.GetProperty("Width").GetDouble(), word.GetProperty("Height").GetDouble(),
                    originalWidth, originalHeight, image.Width, image.Height)))
                .ToList();
            var result = new ImageTextResult(words);
            if (_cache.Count >= 64)
                _cache.Clear();
            _cache[key] = result;
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new([], "Image text could not be read. Review the image crops.");
        }
    }

    /// <summary>A word box in the original image's pixels, top-left origin.</summary>
    private ImageDifferenceRegion ToPixels(double x, double y, double width, double height,
        int originalWidth, int originalHeight, int scaledWidth, int scaledHeight)
    {
        if (ReturnsNormalizedBoxes)
        {
            // Normalized boxes do not depend on the enlargement; only the vertical axis is flipped.
            x *= originalWidth;
            width *= originalWidth;
            height *= originalHeight;
            y = (1 - y) * originalHeight - height;
        }
        else
        {
            var scaleX = (double)originalWidth / scaledWidth;
            var scaleY = (double)originalHeight / scaledHeight;
            x *= scaleX;
            y *= scaleY;
            width *= scaleX;
            height *= scaleY;
        }
        return new ImageDifferenceRegion((int)Math.Floor(x), (int)Math.Floor(y),
            Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)));
    }

    protected static string ReadScript(string fileName)
    {
        using var stream = typeof(LocalImageTextReader).Assembly.GetManifestResourceStream($"DocumentChecker.Engine.Comparison.{fileName}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
