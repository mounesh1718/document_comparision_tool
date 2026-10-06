using DocumentChecker.Engine.Comparison;
using SkiaSharp;
using Xunit;

namespace DocumentChecker.Engine.Tests;

public sealed class ContentDifferenceTests
{
    [Theory]
    [InlineData("Qty 10", "Qty 15", "10", "15")]
    [InlineData("SKU MD-4482", "SKU MD-4482-R", "MD-4482", "MD-4482-R")]
    [InlineData("abc", "Xabc", "abc", "Xabc")]
    [InlineData("Payment in 30 days", "Payment in 45 days", "30", "45")]
    [InlineData("Qty 10  units", "Qty 10 units", " ", "")]
    [InlineData("abc", "abc def", "", " def")]
    [InlineData("abc def", "abc", " def", "")]
    [InlineData("alpha,beta", "alpha beta", ",", " ")]
    [InlineData("controlled release", "controlled-release", " ", "-")]
    public void Text_changes_show_complete_values_without_including_unrelated_text(string before, string after, string expectedBefore, string expectedAfter)
    {
        var change = Assert.Single(TextChangeAnalyzer.Find(before, after));
        Assert.Equal(expectedBefore, before.Substring(change.ReferenceStart, change.ReferenceLength));
        Assert.Equal(expectedAfter, after.Substring(change.CurrentStart, change.CurrentLength));
    }

    [Fact]
    public void Two_changed_values_on_one_line_remain_separate()
    {
        var changes = TextChangeAnalyzer.Find("Quantity 10 price 1,250", "Quantity 15 price 1,750");
        Assert.Equal(2, changes.Count);
        Assert.Equal("10", "Quantity 10 price 1,250".Substring(changes[0].ReferenceStart, changes[0].ReferenceLength));
        Assert.Equal("1,250", "Quantity 10 price 1,250".Substring(changes[1].ReferenceStart, changes[1].ReferenceLength));
    }

    [Fact]
    public void Image_table_values_are_individually_selectable_and_have_row_context()
    {
        var left = new ImageTextResult([Word("Sensor", 10), Word("2", 180), Word("1,250", 280)]);
        var right = new ImageTextResult([Word("Sensor", 10), Word("3", 180), Word("1,750", 280)]);
        var changes = ImageContentComparer.Compare(left, right, [new(180, 30, 20, 18), new(280, 30, 55, 18)]);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, item => item.Reference == "2" && item.Current == "3" && item.ReferenceRegion!.X == 180);
        Assert.Contains(changes, item => item.Reference == "1,250" && item.Current == "1,750" && item.ReferenceRegion!.X == 280);
        Assert.All(changes, item => Assert.Contains("Sensor", item.Context));
    }

    [Fact]
    public void Chart_values_follow_the_category_when_bar_heights_change()
    {
        var axis = new[] { new ImageTextWord("Jan", new(20, 200, 30, 15)), new ImageTextWord("Feb", new(120, 200, 30, 15)), new ImageTextWord("Mar", new(220, 200, 30, 15)) };
        var before = new[] { new ImageTextWord("70", new(20, 110, 25, 15)), new ImageTextWord("81", new(120, 80, 25, 15)), new ImageTextWord("88", new(220, 40, 25, 15)) };
        var after = new[] { new ImageTextWord("70", new(20, 110, 25, 15)), new ImageTextWord("79", new(120, 100, 25, 15)), new ImageTextWord("86", new(220, 65, 25, 15)) };
        var changes = ImageContentComparer.Compare(new(axis.Concat(before).ToList()), new(axis.Concat(after).ToList()),
            [before[1].Bounds, after[1].Bounds, before[2].Bounds, after[2].Bounds]);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, item => item.Reference == "81" && item.Current == "79" && item.Context == "Chart category: Feb");
        Assert.Contains(changes, item => item.Reference == "88" && item.Current == "86" && item.Context == "Chart category: Mar");
    }

    [Fact]
    public void Recognition_differences_without_pixel_changes_are_not_reported()
    {
        var changes = ImageContentComparer.Compare(new([Word("10", 20)]), new([Word("IO", 20)]), []);
        Assert.Empty(changes);
    }

    [Fact]
    public void Unreadable_image_content_keeps_a_visual_change_without_inventing_values()
    {
        var changes = ImageContentComparer.Compare(new([], "OCR unavailable"), new([]), [new(10, 20, 40, 25)]);
        var change = Assert.Single(changes);
        Assert.False(change.IsText);
        Assert.Equal("OCR unavailable", change.ReviewNote);
        Assert.Equal(new ImageDifferenceRegion(10, 20, 40, 25), change.ReferenceRegion);
    }

    [Fact]
    public void Windows_recognition_reads_actual_image_values_and_returns_original_pixel_coordinates()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var reader = new WindowsImageTextReader();
        var reference = reader.Read(CreateTextImage("Payment terms: 30 days"));
        var current = reader.Read(CreateTextImage("Payment terms: 45 days"));
        Assert.Null(reference.Error);
        Assert.Null(current.Error);
        var before = Assert.Single(reference.Words, word => word.Text == "30");
        var after = Assert.Single(current.Words, word => word.Text == "45");
        Assert.InRange(before.Bounds.Y, 10, 65);
        Assert.InRange(before.Bounds.X, 150, 350);
        var changes = ImageContentComparer.Compare(reference, current, [before.Bounds, after.Bounds]);
        Assert.Contains(changes, change => change.Reference == "30" && change.Current == "45" && change.IsText);
    }

    internal static byte[] CreateTextImage(string text)
    {
        using var surface = SKSurface.Create(new SKImageInfo(650, 100));
        surface.Canvas.Clear(SKColors.White);
        using var font = new SKFont(SKTypeface.FromFamilyName("Arial"), 30);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        surface.Canvas.DrawText(text, 20, 55, SKTextAlign.Left, font, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ImageTextWord Word(string text, int x) => new(text, new(x, 30, text.Length * 10, 18));
}
