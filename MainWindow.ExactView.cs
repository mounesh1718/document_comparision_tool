using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;

namespace DocumentChecker.Engine;

/// <summary>
/// Exact pages: each document laid out as its real pages by Microsoft Word or LibreOffice, side by side, with the
/// differences marked on the pages. Also "Open in Word", which opens a read-only copy of a document.
/// </summary>
public partial class MainWindow
{
    private bool _exactMode;
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "DocumentChecker", Guid.NewGuid().ToString("N"));
    // Laid-out documents by file content (SHA-256), so switching views or re-comparing an unchanged file is instant.
    private readonly Dictionary<string, (ExactDocument? Document, string? Error)> _exactCache = [];
    private ExactDocument? _referenceExact;
    private ExactDocument? _currentExact;
    private int _exactRequest;
    private readonly Dictionary<string, Control> _referenceExactAnchors = [];
    private readonly Dictionary<string, Control> _currentExactAnchors = [];
    private readonly List<Canvas> _referenceOverlays = [];
    private readonly List<Canvas> _currentOverlays = [];
    private readonly List<(Border Frame, bool IsReference)> _exactFrames = [];
    private bool _exactSizeHooked;
    private readonly List<Bitmap> _exactBitmaps = [];

    private void ReadingView_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetExactMode(false);

    private void ExactView_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetExactMode(true);

    private void SetExactMode(bool exact)
    {
        ReadingViewButton.IsChecked = !exact;
        ExactViewButton.IsChecked = exact;
        if (_exactMode == exact)
            return;
        _exactMode = exact;
        ReferencePageSurface.IsVisible = CurrentPageSurface.IsVisible = !exact;
        ReferenceExactPanel.IsVisible = CurrentExactPanel.IsVisible = exact;
        // Real pages can be zoomed wider than the pane; Reading view always wraps to the pane.
        ReferenceScrollViewer.HorizontalScrollBarVisibility = CurrentScrollViewer.HorizontalScrollBarVisibility =
            exact ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        if (!_exactSizeHooked)
        {
            _exactSizeHooked = true;
            ReferenceScrollViewer.SizeChanged += (_, _) => SizeExactPages();
            CurrentScrollViewer.SizeChanged += (_, _) => SizeExactPages();
        }
        if (exact)
            _ = ShowExactPagesAsync();
        else
        {
            UpdateExactNotes();
            RenderDocuments();
        }
    }

    /// <summary>Called after every comparison: laid-out pages belong to the compared files, so they are looked up again.</summary>
    private void OnComparisonChanged()
    {
        _referenceExact = _currentExact = null;
        _exactRequest++;
        if (_exactMode)
            _ = ShowExactPagesAsync();
    }

    private async Task ShowExactPagesAsync()
    {
        if (await EnsureExactDocumentsAsync(ShowExactMessage) && _exactMode)
            RenderExactPages();
    }

    /// <summary>
    /// Makes sure both compared documents are laid out as real pages (from the cache when a file is unchanged).
    /// Reports progress and problems through <paramref name="report"/>. False when the pages are not available.
    /// </summary>
    private async Task<bool> EnsureExactDocumentsAsync(Action<string> report)
    {
        if (_comparisonResult is not { IsSuccess: true, ReferenceDocument: { } referenceModel, CurrentDocument: { } currentModel } result ||
            _referencePath is not { } referencePath || _currentPath is not { } currentPath)
        {
            report("Choose the two documents and click Compare to see their exact pages here.");
            return false;
        }
        if (_referenceExact is not null && _currentExact is not null)
            return true;

        var request = ++_exactRequest;
        report("Laying out the pages with Microsoft Word or LibreOffice… This takes a few seconds the first time.");
        var layoutFolder = Path.Combine(_workDirectory, "layout");
        var (reference, current) = await Task.Run(() => (
            Load(result.ReferenceHash, referencePath, referenceModel),
            Load(result.CurrentHash, currentPath, currentModel)));
        if (request != _exactRequest || _closed)
            return _referenceExact is not null && _currentExact is not null;
        if (reference.Document is null || current.Document is null)
        {
            report(reference.Error ?? current.Error ?? PageLayoutConverter.NoEngineMessage);
            return false;
        }
        _referenceExact = reference.Document;
        _currentExact = current.Document;
        return true;

        (ExactDocument? Document, string? Error) Load(string? hash, string path, Reading.DocumentModel model)
        {
            // The laid-out pages depend only on the file; the locator is rebuilt for this comparison's document,
            // which can differ (for example with headers and footers switched off).
            lock (_exactCache)
                if (hash is not null && _exactCache.TryGetValue(hash, out var cached) && cached.Document is { } pages)
                    return (pages with { Locator = new PageLocator(model, pages.Pages) }, null);
            var loaded = ExactDocument.Load(path, model, layoutFolder);
            lock (_exactCache)
                if (hash is not null && loaded.Document is not null)
                    _exactCache[hash] = loaded;
            return loaded;
        }
    }

    private void ShowExactMessage(string message)
    {
        ClearExactPages();
        foreach (var panel in new[] { ReferenceExactPanel, CurrentExactPanel })
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Margin = new Thickness(16, 40),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#475569"))
            });
        UpdateExactNotes();
    }

    private void ClearExactPages()
    {
        ReferenceExactPanel.Children.Clear();
        CurrentExactPanel.Children.Clear();
        _referenceOverlays.Clear();
        _currentOverlays.Clear();
        _referenceExactAnchors.Clear();
        _currentExactAnchors.Clear();
        _exactFrames.Clear();
        foreach (var bitmap in _exactBitmaps)
            bitmap.Dispose();
        _exactBitmaps.Clear();
    }

    private void RenderExactPages()
    {
        ClearExactPages();
        BuildPages(ReferenceExactPanel, _referenceExact!, _referenceOverlays, isReference: true);
        BuildPages(CurrentExactPanel, _currentExact!, _currentOverlays, isReference: false);
        SizeExactPages();
        RefreshExactOverlays();
        ScrollToSelection();
    }

    /// <summary>Each page is its picture with a transparent layer on top for the marks, scaled together to the pane width.</summary>
    private void BuildPages(StackPanel panel, ExactDocument document, List<Canvas> overlays, bool isReference)
    {
        panel.Children.Add(new TextBlock
        {
            Text = $"{document.Pages.Count} page{(document.Pages.Count == 1 ? "" : "s")} · laid out by {document.Engine}" +
                   (_comparisonResult?.UsedOptions.CompareHeadersAndFooters == false ? " · headers and footers are shown as printed but were not compared" : ""),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#64748B")),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        for (var index = 0; index < document.Pages.Count; index++)
        {
            var page = document.Pages[index];
            var bitmap = ToBitmap(page);
            _exactBitmaps.Add(bitmap);
            var overlay = new Canvas { Width = page.Width, Height = page.Height };
            overlays.Add(overlay);
            var surface = new Grid { Width = page.Width, Height = page.Height };
            surface.Children.Add(new Image { Source = bitmap, Width = page.Width, Height = page.Height, Stretch = Stretch.Fill });
            surface.Children.Add(overlay);
            var frame = new Border
            {
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = surface },
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#E2E8F0")),
                BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 1 4 0 #18000000")
            };
            _exactFrames.Add((frame, isReference));
            panel.Children.Add(frame);
            panel.Children.Add(new TextBlock
            {
                Text = $"Page {index + 1} of {document.Pages.Count}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 4)
            });
        }
    }

    internal static Bitmap ToBitmap(PdfPage page)
    {
        var handle = GCHandle.Alloc(page.Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(),
                new PixelSize(page.PixelWidth, page.PixelHeight), new Vector(96, 96), page.PixelWidth * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// At 100% a page fills its pane's width; zoom makes it that much wider (the pane then scrolls sideways),
    /// so small print can be read on the real page.
    /// </summary>
    private void SizeExactPages()
    {
        if (!_exactMode)
            return;
        foreach (var (frame, isReference) in _exactFrames)
        {
            var viewer = isReference ? ReferenceScrollViewer : CurrentScrollViewer;
            var fit = Math.Max(280, (viewer.Viewport.Width > 0 ? viewer.Viewport.Width : viewer.Bounds.Width) - 26);
            frame.Width = Math.Round(fit * _documentZoom);
        }
    }

    /// <summary>Redraws the marks on the pages (after a selection, or when highlights are hidden or shown).</summary>
    private void RefreshExactOverlays()
    {
        if (_comparisonResult is null || _referenceExact is null || _currentExact is null || _referenceOverlays.Count == 0)
            return;
        SizeExactPages();
        DrawMarks(_referenceExact, _referenceOverlays, _referenceExactAnchors, isReference: true);
        DrawMarks(_currentExact, _currentOverlays, _currentExactAnchors, isReference: false);
        UpdateExactNotes();
        ScheduleOverviewUpdate();
    }

    private void DrawMarks(ExactDocument document, List<Canvas> overlays, Dictionary<string, Control> anchors, bool isReference)
    {
        foreach (var overlay in overlays)
            overlay.Children.Clear();
        anchors.Clear();
        // Larger marks first, so smaller marks inside them stay on top and clickable.
        var marks = _comparisonResult!.Differences
            .SelectMany(difference => document.Locator.Locate(difference, isReference).Select(rect => (Difference: difference, Rect: rect, IsGap: false))
                .Concat(document.Locator.Gap(difference, isReference) is { } gap ? [(difference, gap, true)] : []))
            .Where(mark => mark.Rect.Page < overlays.Count)
            .OrderByDescending(mark => mark.Rect.Width * mark.Rect.Height);
        foreach (var (difference, rect, isGap) in marks)
        {
            var mark = CreatePageMark(difference, rect, isGap, isReference);
            overlays[rect.Page].Children.Add(mark);
            if (!anchors.ContainsKey(difference.Id))
                anchors[difference.Id] = mark;
        }
    }

    /// <summary>A mark on a real page, in the same colours as Reading view; clicking it selects the difference.</summary>
    private Control CreatePageMark(DocumentDifference difference, PageRect rect, bool isGap, bool isReference)
    {
        var selected = difference.Id == _selectedDifferenceId;
        var (line, fill) = PageMarkColors(difference, isGap, isReference);
        const double padding = 1.5;
        var mark = new Border
        {
            Width = rect.Width + padding * 2,
            Height = rect.Height + padding * 2,
            CornerRadius = new CornerRadius(1.5),
            Background = _showChangeMarkers ? new SolidColorBrush(Color.Parse(fill)) : Brushes.Transparent,
            BorderBrush = _showChangeMarkers ? new SolidColorBrush(Color.Parse(line)) : Brushes.Transparent,
            BorderThickness = new Thickness(isGap ? 0 : selected ? 1.6 : 0.8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (selected && _showChangeMarkers && !isGap)
            mark.BoxShadow = BoxShadows.Parse($"0 0 0 2 {line}");
        Canvas.SetLeft(mark, rect.X - padding);
        Canvas.SetTop(mark, rect.Y - padding);
        ToolTip.SetTip(mark, isGap
            ? $"{(isReference ? "Added in Current" : "Missing here")}: {ShortLabel(FormatDetailValue(isReference ? difference.CurrentValue : difference.ReferenceValue))}"
            : $"{GetOperation(difference)}\nReference: {FormatDetailValue(difference.ReferenceValue)}\nCurrent: {FormatDetailValue(difference.CurrentValue)}");
        mark.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(mark).Properties.IsLeftButtonPressed)
                return;
            args.Handled = true;
            SelectFromViewer(difference);
        };
        return mark;
    }

    /// <summary>Outline and fill colours of a mark on a real page, matching Reading view.</summary>
    private static (string Line, string Fill) PageMarkColors(DocumentDifference difference, bool isGap, bool isReference) =>
        isGap ? ("#DB2777", "#DB2777")
        : RequiresReview(difference) ? ("#F59E0B", "#33F59E0B")
        : IsImageTableRepresentation(difference) ? ("#64748B", "#2264748B")
        : difference.Category == DifferenceCategory.Formatting || IsMissingFromCurrent(difference) ||
          GetDisplayContentKind(difference, isReference: true) == DifferenceContentKind.Missing ? ("#DB2777", "#26F472B6")
        : isReference ? ("#E11D48", "#26F87171") : ("#16A34A", "#264ADE80");

    /// <summary>In Exact pages, says when the selected difference has no place on a document's pages.</summary>
    private void UpdateExactNotes()
    {
        foreach (var (note, document, isReference) in new[] { (ReferenceExactNote, _referenceExact, true), (CurrentExactNote, _currentExact, false) })
        {
            var difference = _comparisonResult?.Differences.FirstOrDefault(item => item.Id == _selectedDifferenceId);
            var hidden = !_exactMode || document is null || difference is null ||
                document.Locator.Locate(difference, isReference).Count > 0 || document.Locator.Gap(difference, isReference) is not null;
            note.IsVisible = !hidden;
            note.Text = hidden ? string.Empty
                : (isReference ? difference!.ReferenceLocation : difference!.CurrentLocation) is null
                    ? "The selected difference is not in this document."
                    : "The selected difference is not visible on these pages (for example hidden text, or a value only in a header field).";
        }
    }

    private void ScrollToSelection()
    {
        if (_selectedDifferenceId is not { } id)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_selectedDifferenceId != id || _closed)
                return;
            _synchronizingScroll = true;
            try
            {
                if (ActiveAnchors(isReference: true).TryGetValue(id, out var reference))
                    reference.BringIntoView();
                if (ActiveAnchors(isReference: false).TryGetValue(id, out var current))
                    current.BringIntoView();
            }
            finally { _synchronizingScroll = false; }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The marks of the view on screen: on the real pages in Exact pages, in the text in Reading view.</summary>
    private Dictionary<string, Control> ActiveAnchors(bool isReference) => _exactMode
        ? isReference ? _referenceExactAnchors : _currentExactAnchors
        : isReference ? _referenceAnchors : _currentAnchors;

    /// <summary>Opens the selected difference on the real pages of both documents, laying them out first if needed.</summary>
    private async void ShowOnPages_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_comparisonResult is null || _selectedDifferenceId is not { } id)
            return;
        var label = ShowOnPagesButton.Content;
        ShowOnPagesButton.IsEnabled = false;
        ShowOnPagesButton.Content = "Laying out the pages…";
        string? message = null;
        bool ready;
        try
        {
            ready = await EnsureExactDocumentsAsync(text => message = text);
        }
        finally
        {
            ShowOnPagesButton.IsEnabled = true;
            ShowOnPagesButton.Content = label;
        }
        if (!ready)
        {
            StatusText.Text = message ?? PageLayoutConverter.NoEngineMessage;
            return;
        }
        var index = _comparisonResult.Differences.ToList().FindIndex(difference => difference.Id == id);
        if (index >= 0 && !_closed)
            await CreatePageCompareWindow(index).ShowDialog(this);
    }

    /// <summary>The page comparison window for a difference; it describes and colours differences as this window does.</summary>
    internal PageCompareWindow CreatePageCompareWindow(int index) => new(_referenceExact!, _currentExact!, _comparisonResult!.Differences, index,
        new PageCompareWindow.Describers(GetOperation, DetailValue, DescribeSideLocation, PageMarkColors),
        SelectNavigatorDifference);

    private void OpenReference_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenOriginal(_referencePath);

    private void OpenCurrent_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenOriginal(_currentPath);

    /// <summary>Opens a read-only copy of a document in Microsoft Word, or the default app for .docx files.</summary>
    private void OpenOriginal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = "Choose the document first, then open it.";
            return;
        }
        try
        {
            var folder = Path.Combine(_workDirectory, "open", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)} (read-only copy){Path.GetExtension(path)}");
            File.Copy(path, copy);
            File.SetAttributes(copy, FileAttributes.ReadOnly);
            if (OperatingSystem.IsMacOS())
                Process.Start("open", IsWordInstalled() ? ["-a", "Microsoft Word", copy] : [copy]);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
            else
                Process.Start("xdg-open", [copy]);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = "The document could not be opened: " + exception.Message;
        }
    }

    private static bool IsWordInstalled()
    {
        if (OperatingSystem.IsMacOS())
            return Directory.Exists("/Applications/Microsoft Word.app");
        if (OperatingSystem.IsWindows())
            return Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("Word.Application") is not null;
        return false;
    }

    /// <summary>The open buttons say where the document will open.</summary>
    private void LabelOpenButtons()
    {
        var label = IsWordInstalled() ? "Open in Word" : "Open original";
        OpenReferenceButton.Content = label;
        OpenCurrentButton.Content = label;
    }

    /// <summary>Removes the temporary copies and laid-out pages of this session.</summary>
    private void DeleteWorkFiles()
    {
        try
        {
            if (!Directory.Exists(_workDirectory))
                return;
            foreach (var file in Directory.EnumerateFiles(_workDirectory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
