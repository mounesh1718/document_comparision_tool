using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;

namespace DocumentChecker.Engine;

/// <summary>
/// One difference on the real pages: the approved (Reference) page and the Current page side by side, laid out by
/// Word or LibreOffice, with only that difference outlined and the view zoomed to it. Previous / Next step through
/// the differences and keep the main window's selection in step.
/// </summary>
internal sealed class PageCompareWindow : Window
{
    /// <summary>What the window needs from the main window to describe a difference the same way it does.</summary>
    internal sealed record Describers(
        Func<DocumentDifference, string> Operation,
        Func<DocumentDifference, bool, string> Value,
        Func<DocumentDifference, bool, string> Location,
        Func<DocumentDifference, bool, bool, (string Line, string Fill)> Colors);

    private readonly ExactDocument _reference;
    private readonly ExactDocument _current;
    private readonly IReadOnlyList<DocumentDifference> _differences;
    private readonly Describers _describe;
    private readonly Action<DocumentDifference> _selected;
    private readonly PageSide _left;
    private readonly PageSide _right;
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _position = new() { Foreground = Brush("#475569"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, TextAlignment = TextAlignment.Center };
    private readonly SelectableTextBlock _referenceValue = ValueText();
    private readonly SelectableTextBlock _currentValue = ValueText();
    private readonly TextBlock _zoomText = new() { Foreground = Brush("#475569"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private int _index;
    // 1 = the page fits the pane; larger values enlarge it. Null = zoom to the change automatically.
    private double? _zoom;

    public PageCompareWindow(ExactDocument reference, ExactDocument current, IReadOnlyList<DocumentDifference> differences, int index,
        Describers describe, Action<DocumentDifference> selected)
    {
        _reference = reference;
        _current = current;
        _differences = differences;
        _index = Math.Clamp(index, 0, differences.Count - 1);
        _describe = describe;
        _selected = selected;
        _left = new PageSide(isReference: true);
        _right = new PageSide(isReference: false);

        Title = "Difference on the real pages";
        Width = 1320;
        Height = 880;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F3F5F9");
        FontFamily = new FontFamily("Segoe UI, Helvetica Neue, Arial");
        Content = BuildLayout();
        // Caught before the controls inside see them (they would use ← → to move focus or scroll); ↑ ↓ still scroll the page.
        AddHandler(KeyDownEvent, (_, args) =>
        {
            switch (args.Key)
            {
                case Key.Left: Move(-1); args.Handled = true; break;
                case Key.Right: Move(1); args.Handled = true; break;
                case Key.Escape: Close(); args.Handled = true; break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => ShowDifference();
        Closed += (_, _) => { _left.Dispose(); _right.Dispose(); };
    }

    /// <summary>The difference on screen (for tests).</summary>
    internal DocumentDifference Shown => _differences[_index];

    /// <summary>The marks drawn on each page (for tests).</summary>
    internal (int Reference, int Current) MarkCounts => (_left.Marks.Children.Count, _right.Marks.Children.Count);

    private Control BuildLayout()
    {
        var header = new Border
        {
            Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 16,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            _title,
                            new Grid
                            {
                                ColumnDefinitions = new ColumnDefinitions("*,*"),
                                ColumnSpacing = 10,
                                Children =
                                {
                                    ValueBox("APPROVED · REFERENCE", "#9F1239", "#FFF1F2", "#FECDD3", _referenceValue, 0),
                                    ValueBox("CURRENT", "#166534", "#F0FDF4", "#BBF7D0", _currentValue, 1)
                                }
                            }
                        }
                    },
                    Column(Toolbar(), 1)
                }
            }
        };
        var pages = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        pages.Children.Add(_left.Card);
        Grid.SetColumn(_right.Card, 1);
        pages.Children.Add(_right.Card);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10, Margin = new Thickness(14) };
        root.Children.Add(header);
        Grid.SetRow(pages, 1);
        root.Children.Add(pages);
        return root;
    }

    private Control Toolbar()
    {
        var previous = ToolButton("‹ Previous", "Previous difference (←)", () => Move(-1));
        var next = ToolButton("Next ›", "Next difference (→)", () => Move(1));
        var zoomOut = ToolButton("−", "Smaller", () => SetZoom(CurrentZoom() / 1.25));
        var zoomIn = ToolButton("+", "Larger", () => SetZoom(CurrentZoom() * 1.25));
        var whole = ToolButton("Whole page", "Show the whole page", () => SetZoom(1));
        var focus = ToolButton("Zoom to change", "Zoom in on the difference", () => { _zoom = null; Render(); });
        var close = ToolButton("Close", "Close (Esc)", Close);
        return new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { previous, _position, next, close } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { zoomOut, _zoomText, zoomIn, whole, focus } }
            }
        };
    }

    private void Move(int direction)
    {
        if (_differences.Count == 0)
            return;
        _index = (_index + direction + _differences.Count) % _differences.Count;
        _zoom = null;
        ShowDifference();
        _selected(_differences[_index]);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.5, 6);
        Render();
    }

    private double CurrentZoom() => _zoom ?? _left.AppliedZoom;

    private void ShowDifference()
    {
        var difference = _differences[_index];
        _title.Text = _describe.Operation(difference);
        _position.Text = $"{_index + 1} of {_differences.Count}";
        _referenceValue.Text = _describe.Value(difference, true);
        _currentValue.Text = _describe.Value(difference, false);
        Render();
    }

    /// <summary>Draws both pages; both use the same zoom so the two pages can be compared at the same size.</summary>
    private void Render()
    {
        var difference = _differences[_index];
        var left = _left.Prepare(_reference, difference, _describe, otherPage: null);
        var right = _right.Prepare(_current, difference, _describe, otherPage: left);
        if (left is null && right is not null)
            _left.Prepare(_reference, difference, _describe, otherPage: right);

        // Zoom to change: about 3 inches of page around the larger of the two marked areas fill the pane.
        var focusWidth = Math.Max(_left.FocusWidth, _right.FocusWidth);
        var zoom = _zoom ?? Math.Clamp(_left.PageWidth / Math.Max(focusWidth + 140, 230), 1, 4);
        _zoomText.Text = $"{zoom * 100:0}%";
        // A side with nothing to mark shows the same area of its page as the other side.
        var leftFocus = _left.Focus ?? _right.Focus;
        var rightFocus = _right.Focus ?? _left.Focus;
        Dispatcher.UIThread.Post(() =>
        {
            _left.Show(zoom, leftFocus);
            _right.Show(zoom, rightFocus);
        }, DispatcherPriority.Loaded);
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static SelectableTextBlock ValueText() => new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#172B4D") };

    private static Control ValueBox(string caption, string captionColor, string background, string border, Control value, int column)
    {
        var box = new Border
        {
            Background = Brush(background), BorderBrush = Brush(border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7),
            Child = new StackPanel
            {
                Spacing = 2,
                Children = { new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush(captionColor) }, value }
            }
        };
        Grid.SetColumn(box, column);
        return box;
    }

    private static Button ToolButton(string text, string tip, Action click)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(12, 6), Background = Brush("#F1F5F9"), Foreground = Brush("#172B4D"),
            BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6)
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    /// <summary>One document's page: its header, an explanation when the difference is not on it, and the page itself.</summary>
    private sealed class PageSide : IDisposable
    {
        private readonly bool _isReference;
        private readonly TextBlock _heading = new() { FontWeight = FontWeight.Bold };
        private readonly TextBlock _location = new() { FontSize = 12, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _note = new() { FontSize = 12.5, Foreground = Brush("#92400E"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly ScrollViewer _viewer = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("#E9EDF2")
        };
        private readonly Border _frame = new()
        {
            Background = Brushes.White, BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 1 6 0 #22000000"), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly Grid _surface = new();
        private Bitmap? _bitmap;
        private PdfPage? _page;
        private PageRect? _focus;
        private bool _focusIsGap;

        public PageSide(bool isReference)
        {
            _isReference = isReference;
            _heading.Foreground = Brush(isReference ? "#9F1239" : "#166534");
            _surface.Children.Add(_image);
            _surface.Children.Add(Marks);
            _frame.Child = new Viewbox { Stretch = Stretch.Uniform, Child = _surface };
            _viewer.Content = _frame;
            var header = new Border
            {
                Background = Brush(isReference ? "#FFF5F6" : "#F3FCF6"),
                BorderBrush = Brush(isReference ? "#FECDD3" : "#BBF7D0"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 8),
                Child = new StackPanel { Spacing = 2, Children = { _heading, _location, _note } }
            };
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            layout.Children.Add(header);
            Grid.SetRow(_viewer, 1);
            layout.Children.Add(_viewer);
            Card = new Border
            {
                Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                ClipToBounds = true, Child = layout
            };
        }

        public Border Card { get; }
        public Canvas Marks { get; } = new();
        public double PageWidth => _page?.Width ?? 595;
        /// <summary>The width of the marked content (a "missing here" line does not count: it spans the whole text column).</summary>
        public double FocusWidth => _focus is null || _focusIsGap ? 0 : _focus.Width;
        public PageRect? Focus => _focus;
        public double AppliedZoom { get; private set; } = 1;

        /// <summary>
        /// Picks the page that shows the difference and draws its marks. When the difference has no place on this
        /// document's pages, the page at the same position as the other document's is shown with an explanation.
        /// Returns the page shown.
        /// </summary>
        public int? Prepare(ExactDocument document, DocumentDifference difference, Describers describe, int? otherPage)
        {
            var rects = document.Locator.Locate(difference, _isReference);
            var gap = rects.Count == 0 ? document.Locator.Gap(difference, _isReference) : null;
            var target = rects.FirstOrDefault() ?? gap;
            var pageIndex = Math.Clamp(target?.Page ?? otherPage ?? 0, 0, Math.Max(0, document.Pages.Count - 1));
            var side = _isReference ? "Approved · Reference" : "Being checked · Current";
            _heading.Text = $"{side} — page {pageIndex + 1} of {document.Pages.Count}";
            _location.Text = describe.Location(difference, _isReference);
            var exists = (_isReference ? difference.ReferenceLocation : difference.CurrentLocation) is not null;
            _note.IsVisible = target is null || gap is not null;
            _note.Text = gap is not null
                ? $"Not in this document. The pink line shows where the {(_isReference ? "Current" : "approved")} content would be."
                : !exists ? "Not in this document."
                : "Not visible on this document's pages (for example hidden text).";

            _page = document.Pages[pageIndex];
            _bitmap?.Dispose();
            _bitmap = MainWindow.ToBitmap(_page);
            _image.Source = _bitmap;
            _surface.Width = _image.Width = Marks.Width = _page.Width;
            _surface.Height = _image.Height = Marks.Height = _page.Height;

            Marks.Children.Clear();
            var onPage = rects.Where(rect => rect.Page == pageIndex).ToList();
            foreach (var rect in onPage)
                Marks.Children.Add(Mark(rect, describe.Colors(difference, false, _isReference), isGap: false));
            if (gap is not null && gap.Page == pageIndex)
                Marks.Children.Add(Mark(gap, describe.Colors(difference, true, _isReference), isGap: true));
            _focusIsGap = gap is not null && gap.Page == pageIndex;
            var focusRects = _focusIsGap ? [gap!] : onPage;
            _focus = focusRects.Count == 0 ? null : Union(focusRects);
            return target is null ? null : pageIndex;
        }

        /// <summary>Sizes the page for the zoom and scrolls so the difference is in the middle of the pane.</summary>
        public void Show(double zoom, PageRect? focus)
        {
            if (_page is null)
                return;
            AppliedZoom = zoom;
            var fit = Math.Max(300, (_viewer.Viewport.Width > 0 ? _viewer.Viewport.Width : _viewer.Bounds.Width) - 24);
            _frame.Width = Math.Round(fit * zoom);
            Dispatcher.UIThread.Post(() =>
            {
                if (_page is null)
                    return;
                var scale = _frame.Bounds.Width / _page.Width;
                if (focus is null || scale <= 0)
                {
                    _viewer.Offset = default;
                    return;
                }
                var x = (focus.X + focus.Width / 2) * scale + _frame.Margin.Left - _viewer.Viewport.Width / 2;
                var y = (focus.Y + focus.Height / 2) * scale + _frame.Margin.Top - _viewer.Viewport.Height / 2;
                _viewer.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
            }, DispatcherPriority.Background);
        }

        private static Control Mark(PageRect rect, (string Line, string Fill) colors, bool isGap)
        {
            const double padding = 2;
            var mark = new Border
            {
                Width = rect.Width + padding * 2,
                Height = isGap ? Math.Max(3, rect.Height) : rect.Height + padding * 2,
                Background = Brush(colors.Fill),
                BorderBrush = Brush(colors.Line),
                BorderThickness = new Thickness(isGap ? 0 : 1.6),
                CornerRadius = new CornerRadius(2),
                // Avalonia reads 8-digit colours as #AARRGGBB: a soft glow in the mark's own colour.
                BoxShadow = isGap ? default : BoxShadows.Parse($"0 0 0 3 #55{colors.Line.TrimStart('#')}"),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(mark, rect.X - padding);
            Canvas.SetTop(mark, isGap ? rect.Y : rect.Y - padding);
            return mark;
        }

        private static PageRect Union(IReadOnlyList<PageRect> rects)
        {
            var left = rects.Min(rect => rect.X);
            var top = rects.Min(rect => rect.Y);
            return new PageRect(rects[0].Page, left, top, rects.Max(rect => rect.X + rect.Width) - left, rects.Max(rect => rect.Y + rect.Height) - top);
        }

        public void Dispose()
        {
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
