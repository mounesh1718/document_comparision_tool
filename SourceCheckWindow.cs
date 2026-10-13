using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DocumentChecker.Engine.History;
using DocumentChecker.Engine.Sources;

namespace DocumentChecker.Engine;

/// <summary>
/// Source check: choose the document to check and a source folder, click "Proceed checking", and review every fact of
/// every answer: Matches source, Different in source, Not found, Check yourself, with the source page and the
/// evidence boxed on it. Everything runs on this computer.
/// </summary>
internal sealed class SourceCheckWindow : Window
{
    private readonly string _root;
    private string? _documentPath;
    private readonly TextBlock _documentText = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _libraryBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _zoomText = new() { Text = "Fit", VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Center, FontSize = 12.5 };
    private double _zoom = 1;
    private IReadOnlyList<ExactView.PageRect> _boxes = [];
    private double _pageHeight = 1;
    private readonly Button _proceed;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _status = new() { Foreground = Brush("#475569"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly WrapPanel _filters = new() { Orientation = Orientation.Horizontal };
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly StackPanel _details = new() { Spacing = 6 };
    private readonly Grid _pageSurface = new();
    private readonly Canvas _pageMarks = new();
    private readonly Image _pageImage = new() { Stretch = Stretch.Fill };
    private readonly Border _pageFrame;
    private readonly ScrollViewer _pageScroll;
    private readonly List<Control> _whileIdle = [];
    private SourceCheckResult? _result;
    private SourceVerdict? _filter;
    private Bitmap? _pageBitmap;
    private CancellationTokenSource? _working;

    public SourceCheckWindow(string? documentPath = null, string? root = null)
    {
        _root = root ?? SourceLibrary.DefaultRoot;
        Title = "Source check";
        Width = 1320;
        Height = 860;
        MinWidth = 980;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F3F5F9");
        FontFamily = new FontFamily("Segoe UI, Helvetica Neue, Arial");
        _proceed = MakeButton("Proceed checking", "Check every answer of the document against the PDFs of the source folder", () => _ = ProceedAsync(), primary: true);
        _proceed.FontSize = 15;
        _proceed.Padding = new Thickness(20, 9);
        _libraryBox.ItemTemplate = new FuncDataTemplate<SourceLibrary>((library, _) => new TextBlock
        {
            Text = library is null ? "" : $"{library.Name}  ·  {library.Files.Count(file => file.IsReadable)} PDFs",
            TextTrimming = TextTrimming.CharacterEllipsis
        }, supportsRecycling: false);
        _list.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            SourceQuestion question => QuestionItem(question),
            SourceFactResult result => FactItem(result),
            _ => new TextBlock()
        }, supportsRecycling: false);
        _list.SelectionChanged += (_, _) => OnSelection();
        _pageSurface.Children.Add(_pageImage);
        _pageSurface.Children.Add(_pageMarks);
        _pageFrame = new Border
        {
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = _pageSurface },
            Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 1 4 0 #18000000"), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center
        };
        _pageScroll = new ScrollViewer { Content = _pageFrame, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        _pageScroll.SizeChanged += (_, _) => SizePage();
        Content = BuildLayout();
        SetDocument(documentPath);
        ReloadLibraries(AppSettings.Load().SourceFolder);
        ShowDetails(null);
        KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Escape && _working is null)
                Close();
        };
        Closing += (_, _) => _working?.Cancel();
        Closed += (_, _) => _pageBitmap?.Dispose();
    }

    // ---- for tests ----

    internal SourceCheckResult? Result => _result;
    internal string StatusText => _status.Text ?? "";
    internal int ListedFacts => _list.Items.OfType<SourceFactResult>().Count();
    internal int ListedQuestions => _list.Items.OfType<SourceQuestion>().Count();
    internal string DetailsText => string.Join("\n", _details.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
    internal int EvidenceBoxes => _pageMarks.Children.Count;
    internal bool PageShown => _pageFrame.IsVisible;

    internal void SelectLibrary(string name) =>
        _libraryBox.SelectedItem = _libraryBox.Items.OfType<SourceLibrary>().FirstOrDefault(library => library.Name == name);

    internal void SetFilter(SourceVerdict? verdict)
    {
        _filter = verdict;
        ShowResults();
    }

    internal void SelectFact(Func<SourceFactResult, bool> predicate) =>
        _list.SelectedItem = _list.Items.OfType<SourceFactResult>().FirstOrDefault(predicate);

    // ---- layout ----

    private Control BuildLayout()
    {
        var chooseDocument = MakeButton("Choose…", "Choose the question-and-answer document (.docx) to check", ChooseDocument);
        chooseDocument.Background = Brushes.White;
        _whileIdle.Add(chooseDocument);
        var manage = MakeButton("Source folders…", "Create source folders and upload your folders of source PDFs into them", ManageFolders);
        manage.Background = Brushes.White;
        _whileIdle.Add(manage);
        _whileIdle.Add(_libraryBox);
        _whileIdle.Add(_proceed);

        var documentBox = Box("#F3FCF6", "#BBF7D0", "#166534", "② DOCUMENT TO CHECK", _documentText, chooseDocument);
        ToolTip.SetTip(documentBox, "The question-and-answer document (.docx), for example a Quality Overall Summary");
        var libraryBox = Box("#EFF6FF", "#BFDBFE", "#1E40AF", "③ SOURCE FOLDER (PDFs)", _libraryBox, manage);
        var top = Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,1.25*,Auto,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(14, 10),
            Children =
            {
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Margin = new Thickness(0, 0, 6, 0),
                    Children =
                    {
                        new TextBlock { Text = "Source check", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") },
                        new TextBlock { Text = "Every answer checked against your source PDFs · on this computer", FontSize = 12, Foreground = Brush("#64748B") }
                    }
                },
                Column(documentBox, 1),
                Column(libraryBox, 2),
                Column(_proceed, 3),
                Column(MakeButton("Close", "Close (Esc)", Close), 4)
            }
        });

        var export = MakeButton("Export results (CSV)", "Save every fact with its result and source as a CSV file for Excel", () => _ = ExportAsync());
        var nextToReview = MakeButton("Next to review ›", "The next fact that is different, not found or to check yourself", NextToReview, primary: true);
        nextToReview.Background = Brush("#F59E0B");
        nextToReview.BorderBrush = Brush("#F59E0B");
        var resultBar = Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 6,
            Margin = new Thickness(14, 10),
            Children =
            {
                _status,
                Column(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { nextToReview, export } }, 1),
                Row(_filters, 1),
                Span(Row(_progress, 2))
            }
        });

        var listCard = Card(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
            Margin = new Thickness(10),
            Children =
            {
                new TextBlock { Text = "Questions and facts", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), Margin = new Thickness(4, 0) },
                Row(_list, 1)
            }
        });
        var detailCard = Card(new ScrollViewer
        {
            Margin = new Thickness(14, 10),
            Content = _details,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var zoomOut = MakeButton("−", "Smaller", () => Zoom(_zoom / 1.25));
        var zoomIn = MakeButton("+", "Larger, to read small print", () => Zoom(_zoom * 1.25));
        var fit = MakeButton("Fit width", "The whole width of the page", () => Zoom(1));
        var pageHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = "Source page", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) },
                Column(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { zoomOut, _zoomText, zoomIn, fit } }, 1)
            }
        };
        var pageCard = Card(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
            Margin = new Thickness(10),
            Children = { pageHeader, Row(_pageScroll, 1) }
        });
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("1.05*,0.95*,1.2*"), ColumnSpacing = 10 };
        body.Children.Add(listCard);
        body.Children.Add(Column(detailCard, 1));
        body.Children.Add(Column(pageCard, 2));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 10, Margin = new Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(Row(resultBar, 1));
        root.Children.Add(Row(body, 2));
        return root;
    }

    private static Border Box(string background, string border, string captionColor, string caption, Control value, Button action) => new()
    {
        Background = Brush(background), BorderBrush = Brush(border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 7),
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Children =
            {
                new StackPanel
                {
                    Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush(captionColor), TextTrimming = TextTrimming.CharacterEllipsis }, value }
                },
                Column(action, 1)
            }
        }
    };

    // ---- choosing ----

    private void SetDocument(string? path)
    {
        _documentPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _documentText.Text = _documentPath is null ? "Not chosen yet" : Path.GetFileName(_documentPath);
        ToolTip.SetTip(_documentText, _documentPath);
    }

    internal void UseDocument(string path) => SetDocument(path);

    private void ReloadLibraries(string? select)
    {
        var libraries = SourceLibrary.List(_root);
        _libraryBox.ItemsSource = libraries;
        _libraryBox.SelectedItem = libraries.FirstOrDefault(library => library.Name == select) ?? libraries.FirstOrDefault();
        _libraryBox.PlaceholderText = libraries.Count == 0 ? "No source folder yet: click Source folders…" : "Choose a source folder";
        if (_result is null)
            _status.Text = libraries.Count == 0
                ? "First create a source folder and upload your source PDFs: click \"Source folders…\"."
                : "Choose the document and the source folder, then click \"Proceed checking\".";
    }

    private async void ChooseDocument()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the document to check", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Word document") { Patterns = ["*.docx"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SetDocument(path);
    }

    private async void ManageFolders()
    {
        var selected = (_libraryBox.SelectedItem as SourceLibrary)?.Name;
        await new SourceLibraryWindow(_root).ShowDialog(this);
        ReloadLibraries(selected);
    }

    // ---- checking ----

    internal async Task ProceedAsync()
    {
        if (_working is not null)
            return;
        if (_documentPath is null || !File.Exists(_documentPath))
        {
            _status.Text = "Choose the document to check first (② Document to check).";
            return;
        }
        if (_libraryBox.SelectedItem is not SourceLibrary library)
        {
            _status.Text = "Choose a source folder first, or create one with \"Source folders…\" and upload your source PDFs.";
            return;
        }
        (AppSettings.Load() with { SourceFolder = library.Name }).Save();
        _working = new CancellationTokenSource();
        foreach (var control in _whileIdle)
            control.IsEnabled = false;
        _progress.IsVisible = true;
        _progress.Value = 0;
        _status.Text = $"Checking {Path.GetFileName(_documentPath)} against \"{library.Name}\"…";
        var progress = new Progress<SourceCheckProgress>(step =>
        {
            _progress.Value = step.Total == 0 ? 1 : (double)step.Done / step.Total;
            if (step.Current.Length > 0)
                _status.Text = $"Checking question {step.Done + 1} of {step.Total}: {step.Current}";
        });
        var document = _documentPath;
        var token = _working.Token;
        try
        {
            var result = await Task.Run(() => SourceChecker.Check(document, library, progress, token));
            await Task.Yield();
            _result = result;
            _filter = null;
            ShowResults();
            _status.Text = result.Problem ?? Summary(result);
            if (result.Problem is null)
                NextToReview();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "The check was stopped.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = "The check could not be completed: " + exception.Message;
        }
        finally
        {
            _working.Dispose();
            _working = null;
            foreach (var control in _whileIdle)
                control.IsEnabled = true;
            _progress.IsVisible = false;
        }
    }

    private static string Summary(SourceCheckResult result) =>
        $"{result.Questions.Count} question{(result.Questions.Count == 1 ? "" : "s")}, {result.Results.Count} facts checked against \"{result.LibraryName}\": " +
        $"{result.Count(SourceVerdict.Matches)} match, {result.Count(SourceVerdict.Different)} different, " +
        $"{result.Count(SourceVerdict.NotFound)} not found, {result.Count(SourceVerdict.CheckYourself)} to check yourself.";

    // ---- results ----

    private void ShowResults()
    {
        _filters.Children.Clear();
        _list.ItemsSource = null;
        _list.Items.Clear();
        if (_result is null || _result.Problem is not null)
        {
            ShowDetails(null);
            return;
        }
        AddFilter(null, $"All {_result.Results.Count}", "#334155");
        foreach (var verdict in new[] { SourceVerdict.Different, SourceVerdict.NotFound, SourceVerdict.CheckYourself, SourceVerdict.Matches })
            AddFilter(verdict, $"{Symbol(verdict)} {SourceCheckResult.VerdictText(verdict)} {_result.Count(verdict)}", Colors(verdict).Text);

        foreach (var question in _result.Questions)
        {
            var facts = _result.Results.Where(result => result.Fact.QuestionNumber == question.Number && (_filter is null || result.Verdict == _filter)).ToList();
            if (facts.Count == 0 && _filter is not null)
                continue;
            _list.Items.Add(question);
            foreach (var fact in facts)
                _list.Items.Add(fact);
        }
        ShowDetails(null);
    }

    private void AddFilter(SourceVerdict? verdict, string text, string color)
    {
        var selected = _filter == verdict;
        var button = new ToggleButton
        {
            Content = text, IsChecked = selected, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4), FontSize = 12.5,
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Background = selected ? Brush(color) : Brushes.White, Foreground = selected ? Brushes.White : Brush(color), BorderBrush = Brush(color)
        };
        button.Click += (_, _) => SetFilter(verdict);
        _filters.Children.Add(button);
    }

    private Control QuestionItem(SourceQuestion question)
    {
        var counts = _result!.Results.Where(result => result.Fact.QuestionNumber == question.Number).GroupBy(result => result.Verdict)
            .OrderBy(group => group.Key).Select(group => $"{Symbol(group.Key)} {group.Count()}");
        return new StackPanel
        {
            Margin = new Thickness(4, 10, 4, 2), Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = $"QUESTION {question.Number}{(question.Section.Length > 0 ? " · " + question.Section : "")}   {string.Join("  ", counts)}",
                    FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush("#64748B")
                },
                new TextBlock { Text = question.Text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        };
    }

    private static Control FactItem(SourceFactResult result)
    {
        var (text, background) = Colors(result.Verdict);
        var title = result.Fact.Kind == SourceFactKind.TableRow ? $"{result.Fact.Label}: {result.Fact.Value}" : result.Fact.Value;
        return new Border
        {
            Margin = new Thickness(10, 1, 2, 1), Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(6), Background = Brush(background),
            BorderBrush = Brush(text), BorderThickness = new Thickness(3, 0, 0, 0),
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = $"{Symbol(result.Verdict)} {SourceCheckResult.VerdictText(result.Verdict)}", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush(text) },
                    new TextBlock { Text = title, FontSize = 12.5, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
    }

    private void OnSelection()
    {
        switch (_list.SelectedItem)
        {
            case SourceQuestion question:
                // A question heading selects its first fact.
                var first = _list.Items.OfType<SourceFactResult>().FirstOrDefault(result => result.Fact.QuestionNumber == question.Number);
                if (first is not null)
                    _list.SelectedItem = first;
                break;
            case SourceFactResult result:
                ShowDetails(result);
                break;
            default:
                ShowDetails(null);
                break;
        }
    }

    private void NextToReview()
    {
        var facts = _list.Items.OfType<SourceFactResult>().ToList();
        var start = _list.SelectedItem is SourceFactResult current ? facts.IndexOf(current) + 1 : 0;
        var next = facts.Skip(start).Concat(facts.Take(start)).FirstOrDefault(result => result.Verdict != SourceVerdict.Matches);
        if (next is not null)
        {
            _list.SelectedItem = next;
            _list.ScrollIntoView(next);
        }
        else if (_result is not null)
            _status.Text = Summary(_result) + " Nothing left to review.";
    }

    private void ShowDetails(SourceFactResult? result)
    {
        _details.Children.Clear();
        ShowPage(null);
        if (result is null)
        {
            _details.Children.Add(new TextBlock
            {
                Text = _result is null
                    ? "How it works: every answer is split into facts (each row of an answer table, each value in a sentence). " +
                      "Each fact is looked up in the source PDFs, in the matching section first (2.3.S.4 → 3.2.S.4), and its value is compared with the value printed next to the same label.\n\n" +
                      "✓ Matches source · ✗ Different in source · ⚠ Not found in sources · ? Check yourself\n\nClick a fact to see the source page with the evidence boxed."
                    : "Click a fact on the left to see its source.",
                FontSize = 13, Foreground = Brush("#475569"), TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        var question = _result!.Questions.First(item => item.Number == result.Fact.QuestionNumber);
        var (color, background) = Colors(result.Verdict);
        Caption($"QUESTION {question.Number}{(question.Section.Length > 0 ? " · " + question.Section : "")}");
        Text(question.Text, 13, FontWeight.SemiBold);
        Caption("IN THE DOCUMENT");
        if (result.Fact.Label.Length > 0)
            Text(result.Fact.Label, 12.5, FontWeight.SemiBold, "#475569");
        Text(result.Fact.Value, 15, FontWeight.SemiBold);
        if (result.Fact.Kind != SourceFactKind.TableRow && result.Fact.Text != result.Fact.Value)
            Text("Sentence: " + result.Fact.Text, 12, FontWeight.Normal, "#64748B");
        _details.Children.Add(new Border
        {
            Background = Brush(background), BorderBrush = Brush(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8), Margin = new Thickness(0, 8, 0, 0),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"{Symbol(result.Verdict)} {SourceCheckResult.VerdictText(result.Verdict)}", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Brush(color) },
                    new TextBlock { Text = result.Explanation, FontSize = 12.5, Foreground = Brush("#334155"), TextWrapping = TextWrapping.Wrap }
                }
            }
        });
        if (result.SourceValue is { Length: > 0 } sourceValue)
        {
            Caption("IN THE SOURCE");
            _details.Children.Add(new SelectableTextBlock { Text = sourceValue, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap });
        }
        if (result.Evidence is { } evidence)
        {
            Caption("SOURCE PDF");
            Text(evidence.File.RelativePath, 12.5, FontWeight.Normal, "#334155");
            Text($"Page {evidence.Page}{(evidence.Ocr ? " (scanned page, read by text recognition)" : "")}", 12.5, FontWeight.SemiBold, "#334155");
            var open = MakeButton("Open source PDF", "Open a read-only copy of this source PDF", () => OpenSource(evidence));
            open.Margin = new Thickness(0, 6, 0, 0);
            _details.Children.Add(open);
            ShowPage(result);
        }
        else
            Text(result.Verdict == SourceVerdict.NotFound ? "No source page to show." : "", 12.5, FontWeight.Normal, "#64748B");

        void Caption(string text) => _details.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush("#64748B"), Margin = new Thickness(0, 8, 0, 0)
        });

        void Text(string text, double size, FontWeight weight, string colour = "#172B4D") => _details.Children.Add(new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, Foreground = Brush(colour), TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>Draws the source page with the evidence boxed in the result's colour, and scrolls to it.</summary>
    private void ShowPage(SourceFactResult? result)
    {
        _pageMarks.Children.Clear();
        _boxes = [];
        _pageImage.Source = null;
        _pageBitmap?.Dispose();
        _pageBitmap = null;
        _pageFrame.IsVisible = false;
        if (result?.Evidence is not { } evidence || _libraryBox.Items.OfType<SourceLibrary>().FirstOrDefault(library => library.Name == _result?.LibraryName) is not { } library)
            return;
        try
        {
            var (page, boxes) = SourcePages.Draw(library.PathOf(evidence.File), evidence);
            _pageBitmap = MainWindow.ToBitmap(page);
            _pageImage.Source = _pageBitmap;
            _pageSurface.Width = _pageImage.Width = _pageMarks.Width = page.Width;
            _pageSurface.Height = _pageImage.Height = _pageMarks.Height = page.Height;
            var (color, _) = Colors(result.Verdict);
            foreach (var box in boxes)
            {
                var mark = new Border
                {
                    Width = box.Width + 4, Height = box.Height + 4, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1.4),
                    BorderBrush = Brush(color), Background = Brush("#30" + color[1..])
                };
                Canvas.SetLeft(mark, box.X - 2);
                Canvas.SetTop(mark, box.Y - 2);
                _pageMarks.Children.Add(mark);
            }
            _boxes = boxes;
            _pageHeight = page.Height;
            _pageFrame.IsVisible = true;
            SizePage();
            ScrollToEvidence();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Text("The source page could not be drawn: " + exception.Message);
        }

        void Text(string text) => _details.Children.Add(new TextBlock { Text = text, Foreground = Brush("#B91C1C"), TextWrapping = TextWrapping.Wrap });
    }

    /// <summary>At "Fit width" the page fills the pane; zoom makes it wider (the pane then scrolls sideways).</summary>
    private void SizePage()
    {
        var width = _pageScroll.Viewport.Width > 0 ? _pageScroll.Viewport.Width : _pageScroll.Bounds.Width;
        if (width > 0)
            _pageFrame.Width = Math.Round(Math.Max(240, width - 8) * _zoom);
    }

    private void Zoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 1, 4);
        _zoomText.Text = _zoom == 1 ? "Fit" : $"{_zoom * 100:0}%";
        SizePage();
        ScrollToEvidence();
    }

    /// <summary>Scrolls the page so the first evidence box is in view, a third of the way down.</summary>
    private void ScrollToEvidence()
    {
        if (_boxes.Count == 0)
            return;
        var box = _boxes[0];
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var frame = _pageFrame.Bounds;
            if (frame.Height <= 0 || _pageSurface.Width is not > 0)
                return;
            var scale = frame.Height / _pageHeight;
            _pageScroll.Offset = new Vector(
                Math.Max(0, box.X * scale - 24),
                Math.Max(0, box.Y * scale - _pageScroll.Viewport.Height / 3));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OpenSource(SourceEvidence evidence)
    {
        if (_libraryBox.Items.OfType<SourceLibrary>().FirstOrDefault(library => library.Name == _result?.LibraryName) is not { } library)
            return;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "DocumentChecker", "source-open", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, evidence.File.Name);
            File.Copy(library.PathOf(evidence.File), copy);
            File.SetAttributes(copy, FileAttributes.ReadOnly);
            if (OperatingSystem.IsMacOS())
                Process.Start("open", [copy]);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
            else
                Process.Start("xdg-open", [copy]);
            _status.Text = $"Opened a read-only copy of {evidence.File.Name}; the evidence is on page {evidence.Page}.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _status.Text = "The source PDF could not be opened: " + exception.Message;
        }
    }

    private async Task ExportAsync()
    {
        if (_result is null || _result.Problem is not null)
        {
            _status.Text = "Run the check first, then export its results.";
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the source check as CSV",
                SuggestedFileName = $"Source-check-{Path.GetFileNameWithoutExtension(_result.DocumentPath)}-{_result.CheckedAt.ToLocalTime():yyyy-MM-dd-HHmm}.csv",
                FileTypeChoices = [new FilePickerFileType("CSV (Excel)") { Patterns = ["*.csv"] }]
            });
            if (file?.TryGetLocalPath() is not { } path)
                return;
            // A byte-order mark lets Excel open the file as UTF-8 (symbols such as ≤ and µ stay correct).
            await File.WriteAllTextAsync(path, _result.ToCsv(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _status.Text = $"Saved {path}";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _status.Text = "The CSV file could not be saved: " + exception.Message;
        }
    }

    // ---- helpers ----

    private static string Symbol(SourceVerdict verdict) => verdict switch
    {
        SourceVerdict.Matches => "✓",
        SourceVerdict.Different => "✗",
        SourceVerdict.NotFound => "⚠",
        _ => "?"
    };

    private static (string Text, string Background) Colors(SourceVerdict verdict) => verdict switch
    {
        SourceVerdict.Matches => ("#15803D", "#F0FDF4"),
        SourceVerdict.Different => ("#B91C1C", "#FEF2F2"),
        SourceVerdict.NotFound => ("#C2410C", "#FFF7ED"),
        _ => ("#A16207", "#FEFCE8")
    };

    private static Border Card(Control child) => new()
    {
        Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = child
    };

    private static Button MakeButton(string text, string tip, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Background = Brush(primary ? "#2457C5" : "#F1F5F9"), Foreground = primary ? Brushes.White : Brush("#172B4D"),
            BorderBrush = Brush(primary ? "#2457C5" : "#E2E8F0"), VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control Row(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static Control Span(Control control)
    {
        Grid.SetColumnSpan(control, 2);
        return control;
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
