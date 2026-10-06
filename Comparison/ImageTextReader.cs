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

/// <summary>Runs the Windows OCR runtime locally through its Windows PowerShell host.</summary>
public sealed class WindowsImageTextReader : IImageTextReader
{
    private readonly Dictionary<string, ImageTextResult> _cache = new();

    public ImageTextResult Read(byte[] imageData)
    {
        if (!OperatingSystem.IsWindows())
            return new([], "Image text recognition requires Windows. Review the image crops.");
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
            using var scriptStream = typeof(WindowsImageTextReader).Assembly.GetManifestResourceStream("DocumentChecker.Engine.Comparison.ReadImageText.ps1")!;
            using var scriptReader = new StreamReader(scriptStream);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(scriptReader.ReadToEnd())));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.WriteLine(Convert.ToBase64String(buffer.ToArray()));
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
            var scaleX = (double)originalWidth / image.Width;
            var scaleY = (double)originalHeight / image.Height;
            var words = json.RootElement.GetProperty("Words").EnumerateArray().Select(word =>
                new ImageTextWord(word.GetProperty("Text").GetString()!, new ImageDifferenceRegion(
                    (int)Math.Floor(word.GetProperty("X").GetDouble() * scaleX),
                    (int)Math.Floor(word.GetProperty("Y").GetDouble() * scaleY),
                    Math.Max(1, (int)Math.Ceiling(word.GetProperty("Width").GetDouble() * scaleX)),
                    Math.Max(1, (int)Math.Ceiling(word.GetProperty("Height").GetDouble() * scaleY))))).ToList();
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
}
