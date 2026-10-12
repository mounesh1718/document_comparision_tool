using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;

namespace DocumentChecker.Engine;

/// <summary>
/// The strip beside each document's scroll bar: one mark per finding at its position in the document,
/// plus a shaded box for the part currently on screen. Review items are amber so they can be found while scrolling.
/// </summary>
public partial class MainWindow
{
    private const string ReviewMarkColor = "#F59E0B";
    private Border? _referenceViewport;
    private Border? _currentViewport;
    private bool _overviewPending;

    private void HookOverviewUpdates()
    {
        ReferenceOverview.SizeChanged += (_, _) => ScheduleOverviewUpdate();
        CurrentOverview.SizeChanged += (_, _) => ScheduleOverviewUpdate();
        ReferencePageSurface.SizeChanged += (_, _) => ScheduleOverviewUpdate();
        CurrentPageSurface.SizeChanged += (_, _) => ScheduleOverviewUpdate();
    }

    private void ScheduleOverviewUpdate()
    {
        if (_overviewPending)
            return;
        _overviewPending = true;
        // Background priority runs after layout, when the anchors have their final positions.
        Dispatcher.UIThread.Post(() =>
        {
            _overviewPending = false;
            UpdateOverviews();
        }, DispatcherPriority.Background);
    }

    private void UpdateOverviews()
    {
        if (_closed)
            return;
        _referenceViewport = DrawOverview(ReferenceOverview, ReferenceScrollViewer,
            _exactMode ? ReferenceExactPanel : ReferencePageSurface, ActiveAnchors(isReference: true), isReference: true);
        _currentViewport = DrawOverview(CurrentOverview, CurrentScrollViewer,
            _exactMode ? CurrentExactPanel : CurrentPageSurface, ActiveAnchors(isReference: false), isReference: false);
    }

    private Border? DrawOverview(Canvas canvas, ScrollViewer viewer, Control surface, IDictionary<string, Control> anchors, bool isReference)
    {
        canvas.Children.Clear();
        var height = canvas.Bounds.Height;
        var width = canvas.Bounds.Width;
        var extent = viewer.Extent.Height;
        if (_comparisonResult is null || height <= 0 || extent <= 0)
            return null;

        var viewport = new Border
        {
            Width = width,
            Background = new SolidColorBrush(Color.Parse("#3364748B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#64748B")),
            BorderThickness = new Thickness(0, 1),
            IsHitTestVisible = false
        };
        canvas.Children.Add(viewport);
        PositionViewport(viewport, viewer, height);

        // Selected and review marks are drawn last so they stay on top of ordinary changes.
        var marks = _comparisonResult.Differences
            .Where(difference => anchors.ContainsKey(difference.Id))
            .OrderBy(difference => difference.Id == _selectedDifferenceId ? 2 : RequiresReview(difference) ? 1 : 0);
        foreach (var difference in marks)
        {
            var anchor = anchors[difference.Id];
            if (anchor.TranslatePoint(new Point(0, 0), surface) is not { } position)
                continue;
            var top = (position.Y + surface.Margin.Top) / extent * height;
            var selected = difference.Id == _selectedDifferenceId;
            var mark = new Border
            {
                Width = selected ? width : width - 4,
                Height = Math.Max(selected ? 5 : 3, anchor.Bounds.Height / extent * height),
                Background = new SolidColorBrush(Color.Parse(selected ? "#1E293B" : MarkColor(difference, isReference))),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            Canvas.SetLeft(mark, selected ? 0 : 2);
            Canvas.SetTop(mark, Math.Clamp(top, 0, Math.Max(0, height - mark.Height)));
            ToolTip.SetTip(mark, $"{GetOperation(difference)}\n{DescribeSideLocation(difference, isReference)}");
            mark.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(mark).Properties.IsLeftButtonPressed)
                    return;
                args.Handled = true;
                SelectNavigatorDifference(difference);
            };
            canvas.Children.Add(mark);
        }
        return viewport;
    }

    private static string MarkColor(DocumentDifference difference, bool isReference)
    {
        if (RequiresReview(difference))
            return ReviewMarkColor;
        var value = isReference ? difference.ReferenceValue : difference.CurrentValue;
        if (difference.Category == DifferenceCategory.Formatting || value.StartsWith("(no corresponding", StringComparison.Ordinal))
            return "#EC4899";
        return isReference ? "#EF4444" : "#22C55E";
    }

    private void UpdateViewportIndicators()
    {
        if (_referenceViewport is not null)
            PositionViewport(_referenceViewport, ReferenceScrollViewer, ReferenceOverview.Bounds.Height);
        if (_currentViewport is not null)
            PositionViewport(_currentViewport, CurrentScrollViewer, CurrentOverview.Bounds.Height);
    }

    private static void PositionViewport(Border viewport, ScrollViewer viewer, double height)
    {
        var extent = viewer.Extent.Height;
        if (extent <= 0 || height <= 0)
            return;
        viewport.Height = Math.Max(4, Math.Min(height, viewer.Viewport.Height / extent * height));
        Canvas.SetTop(viewport, Math.Clamp(viewer.Offset.Y / extent * height, 0, Math.Max(0, height - viewport.Height)));
    }

    private void NextReview_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var reviews = ReviewDifferences();
        if (reviews.Count == 0)
            return;
        var index = reviews.FindIndex(difference => difference.Id == _selectedDifferenceId);
        SelectNavigatorDifference(reviews[(index + 1) % reviews.Count]);
    }

    private List<DocumentDifference> ReviewDifferences() =>
        _comparisonResult?.Differences.Where(RequiresReview).ToList() ?? [];

    private void UpdateReviewButton()
    {
        var reviews = ReviewDifferences();
        NextReviewButton.IsVisible = reviews.Count > 0;
        var position = reviews.FindIndex(difference => difference.Id == _selectedDifferenceId);
        NextReviewButton.Content = position < 0
            ? $"Next to review ({reviews.Count})"
            : $"Next to review ({position + 1} of {reviews.Count})";
    }
}
