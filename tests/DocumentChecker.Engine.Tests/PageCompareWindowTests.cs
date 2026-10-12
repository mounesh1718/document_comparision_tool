using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;
using DocumentChecker.Engine.Reading;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>"Show on the real pages": one difference on both documents' pages. Runs inside the shared UI test window.</summary>
internal static class PageCompareWindowTests
{
    internal static void Verify()
    {
        var reference = Document(("Assay 95.0% of label claim", 140), ("Keep out of reach of children.", 170));
        var current = Document(("Assay 90.0% of label claim", 140));
        DocumentDifference Text(string id, int paragraph, int start, int length, bool onCurrent) => new(id, DifferenceCategory.Text, "Text", "a", "b",
            new(paragraph, 0), onCurrent ? new(paragraph, 0) : new(1, 0), new(paragraph, start, length), onCurrent ? new(paragraph, start, length) : new(1, 0, 0), "");
        // 95.0% -> 90.0% is on both pages; the second difference (text hidden in Current) only on the approved page.
        var differences = new[] { Text("TXT-001", 0, 6, 5, onCurrent: true), Text("TXT-002", 1, 0, 4, onCurrent: false) };
        var selected = new List<DocumentDifference>();
        var window = new PageCompareWindow(reference, current, differences, 0,
            new PageCompareWindow.Describers(d => d.Id, (d, isReference) => isReference ? d.ReferenceValue : d.CurrentValue, (_, _) => "", (_, _, _) => ("#E11D48", "#26F87171")),
            selected.Add);
        window.Show();
        try
        {
            Layout(window);
            Assert.Same(differences[0], window.Shown);
            Assert.Equal((1, 1), window.MarkCounts);

            // → shows the next difference and keeps the main window's selection in step.
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Layout(window);
            Assert.Same(differences[1], window.Shown);
            Assert.Equal(differences[1], Assert.Single(selected));
            // Only the approved page has a place for it; the Current page shows the same area without a wrong mark.
            Assert.Equal((1, 0), window.MarkCounts);

            // ← wraps back to the first difference.
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            Layout(window);
            Assert.Same(differences[0], window.Shown);
        }
        finally
        {
            window.Close();
        }
    }

    private static ExactDocument Document(params (string Text, double Y)[] lines)
    {
        var format = new RunFormatting(null, null, null, null, null, null, null, null, null, null, null);
        var model = new DocumentModel("test.docx", lines.Select((line, index) => new ParagraphBlock(index, [new TextRun(0, line.Text, format)])).ToList());
        var characters = new List<PdfCharacter>();
        foreach (var (text, y) in lines)
        {
            var x = 10d;
            foreach (var character in text.Where(character => !char.IsWhiteSpace(character)))
            {
                characters.Add(new PdfCharacter(character, x, y, 6, 10));
                x += 6;
            }
        }
        var page = new PdfPage(595, 842, 2, 2, new byte[16], characters, []);
        return new ExactDocument("Test", [page], new PageLocator(model, [page]));
    }

    private static void Layout(Window window)
    {
        for (var pass = 0; pass < 6; pass++)
        {
            window.Measure(new Size(1320, 880));
            window.Arrange(new Rect(0, 0, 1320, 880));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
