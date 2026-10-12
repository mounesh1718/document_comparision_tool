using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DocumentChecker.Engine.History;

namespace DocumentChecker.Engine;

/// <summary>
/// The comparisons made on this computer: a searchable list, the details of the selected comparison (files and
/// fingerprints, whether the files changed since, the result and every difference found), and actions to compare the
/// files again, export to CSV, or delete entries.
/// </summary>
internal sealed class HistoryWindow : Window
{
    private readonly ComparisonHistory _history;
    private readonly Action<HistoryEntry> _compareAgain;
    private readonly TextBox _search = new() { Watermark = "Search by file name, result or reviewer…", MinWidth = 280 };
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly TextBlock _count = new() { Foreground = Brush("#64748B"), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _details = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { Foreground = Brush("#475569"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private IReadOnlyList<HistoryEntry> _entries = [];

    public HistoryWindow(ComparisonHistory history, Action<HistoryEntry> compareAgain)
    {
        _history = history;
        _compareAgain = compareAgain;
        Title = "Comparison history";
        Width = 1180;
        Height = 780;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F3F5F9");
        FontFamily = new FontFamily("Segoe UI, Helvetica Neue, Arial");
        _list.ItemTemplate = new FuncDataTemplate<HistoryEntry>((entry, _) => entry is null ? new TextBlock() : ListItem(entry), supportsRecycling: false);
        _list.SelectionChanged += (_, _) => ShowDetails(_list.SelectedItem as HistoryEntry);
        _search.TextChanged += (_, _) => Filter();
        Content = BuildLayout();
        Reload();
        KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Escape)
                Close();
        };
    }

    /// <summary>The entries listed after filtering (for tests).</summary>
    internal int ListedCount => _list.ItemCount;

    /// <summary>Selects an entry, as a click in the list would (for tests).</summary>
    internal void Select(int index) => _list.SelectedIndex = index;

    /// <summary>The text of the details panel (for tests).</summary>
    internal string DetailsText => string.Join("\n", _details.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    /// <summary>Runs "Compare these files again" for the selected entry (for tests).</summary>
    internal void CompareSelectedAgain()
    {
        if (_list.SelectedItem is HistoryEntry entry)
            CompareAgain(entry);
    }

    private Control BuildLayout()
    {
        var top = new Border
        {
            Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 12,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock { Text = "Comparison history", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") },
                            new TextBlock
                            {
                                Text = $"Every comparison on this computer is saved here, with the files' fingerprints and the differences found. Saved in {_history.FilePath}",
                                FontSize = 12, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap
                            }
                        }
                    },
                    Column(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            _search,
                            MakeButton("Export all (CSV)", "Save the list of comparisons as a CSV file for Excel", ExportAll),
                            MakeButton("Clear history", "Delete all saved comparisons from this computer", ClearAll),
                            MakeButton("Close", "Close (Esc)", Close)
                        }
                    }, 1)
                }
            }
        };

        var listCard = Card(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Margin = new Thickness(12),
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { new TextBlock { Text = "Comparisons", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") }, Column(_count, 1) }
                },
                Row(_list, 1)
            }
        });
        var detailsCard = Card(new ScrollViewer
        {
            Margin = new Thickness(16, 12),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _details
        });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*"), ColumnSpacing = 10 };
        body.Children.Add(listCard);
        body.Children.Add(Column(detailsCard, 1));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(Row(body, 1));
        root.Children.Add(Row(_status, 2));
        return root;
    }

    private void Reload()
    {
        _entries = _history.Load();
        Filter();
    }

    private void Filter()
    {
        var words = (_search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _entries.Where(entry => words.All(word =>
            new[] { entry.Reference.Name, entry.Current.Name, entry.Result, entry.Reviewer, entry.ComparedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
                .Any(text => text.Contains(word, StringComparison.OrdinalIgnoreCase)))).ToList();
        _list.ItemsSource = shown;
        _count.Text = shown.Count == _entries.Count ? $"{_entries.Count} saved" : $"{shown.Count} of {_entries.Count}";
        if (shown.Count > 0)
            _list.SelectedIndex = 0;
        else
            ShowDetails(null);
    }

    private static Control ListItem(HistoryEntry entry)
    {
        var accent = entry.Status switch
        {
            "ExactFileMatch" or "DocumentDataMatch" => "#16A34A",
            _ when entry.ToCheck > 0 => "#F59E0B",
            _ => "#60A5FA"
        };
        return new Border
        {
            Margin = new Thickness(0, 2), Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("4,*"),
                Children =
                {
                    new Border { Background = Brush(accent) },
                    Column(new StackPanel
                    {
                        Spacing = 2,
                        Margin = new Thickness(10, 7),
                        Children =
                        {
                            new TextBlock { Text = entry.ComparedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.CurrentCulture), FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") },
                            new TextBlock { Text = entry.Result, FontSize = 12.5, Foreground = Brush(entry.ToCheck > 0 ? "#92400E" : "#334155") },
                            new TextBlock { Text = $"{entry.Reference.Name}  →  {entry.Current.Name}", FontSize = 12, Foreground = Brush("#475569"), TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = $"by {entry.Reviewer} on {entry.Computer}", FontSize = 11, Foreground = Brush("#94A3B8") }
                        }
                    }, 1)
                }
            }
        };
    }

    private void ShowDetails(HistoryEntry? entry)
    {
        _details.Children.Clear();
        if (entry is null)
        {
            _details.Children.Add(new TextBlock
            {
                Text = _entries.Count == 0 ? "No comparisons yet. Each comparison you make is saved here automatically." : "No comparison matches the search.",
                Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 30)
            });
            return;
        }

        _details.Children.Add(new TextBlock { Text = entry.Result, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") });
        _details.Children.Add(new TextBlock
        {
            Text = $"Compared on {entry.ComparedAt.ToLocalTime().ToString("dddd d MMMM yyyy 'at' HH:mm:ss", CultureInfo.CurrentCulture)} by {entry.Reviewer} on {entry.Computer}",
            Foreground = Brush("#475569"), TextWrapping = TextWrapping.Wrap
        });
        var files = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        files.Children.Add(FileBox("APPROVED · REFERENCE", "#9F1239", "#FFF5F6", "#FECDD3", entry.Reference));
        files.Children.Add(Column(FileBox("CURRENT", "#166534", "#F3FCF6", "#BBF7D0", entry.Current), 1));
        _details.Children.Add(files);
        _details.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                MakeButton("Compare these files again", "Open these two files in the main window and compare them now", () => CompareAgain(entry), primary: true),
                MakeButton("Export this comparison (CSV)", "Save this comparison's differences as a CSV file", () => ExportOne(entry)),
                MakeButton("Delete from history", "Remove this comparison from the history", () => DeleteOne(entry))
            }
        });

        if (entry.ByChange.Count > 0)
        {
            _details.Children.Add(Caption("DIFFERENCES BY TYPE"));
            var types = new WrapPanel();
            foreach (var (change, count) in entry.ByChange)
                types.Children.Add(new Border
                {
                    Background = Brush("#F1F5F9"), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3), Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock { Text = $"{change}  {count}", FontSize = 12, Foreground = Brush("#334155") }
                });
            _details.Children.Add(types);
        }

        if (entry.Findings.Count > 0)
        {
            _details.Children.Add(Caption($"ALL {entry.Findings.Count} DIFFERENCES, AS FOUND THEN"));
            foreach (var finding in entry.Findings)
                _details.Children.Add(new Border
                {
                    Background = Brushes.White, BorderBrush = Brush(finding.CheckYourself ? "#FCD34D" : "#E2E8F0"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
                    Child = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = $"{finding.Change}{(finding.CheckYourself ? "  ·  check yourself" : "")}", FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") },
                            new SelectableTextBlock { Text = $"Reference: {finding.Reference}", Foreground = Brush("#9F1239"), TextWrapping = TextWrapping.Wrap },
                            new SelectableTextBlock { Text = $"Current: {finding.Current}", Foreground = Brush("#166534"), TextWrapping = TextWrapping.Wrap },
                            new TextBlock
                            {
                                Text = finding.ReferenceLocation == finding.CurrentLocation ? finding.ReferenceLocation : $"{finding.ReferenceLocation}  /  {finding.CurrentLocation}",
                                FontSize = 11, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                });
        }
    }

    /// <summary>A compared file: its name, folder, fingerprint, and whether it is still the same file today.</summary>
    private static Control FileBox(string caption, string color, string background, string border, HistoryFile file)
    {
        var state = ComparisonHistory.StateOf(file);
        var (stateText, stateColor) = state switch
        {
            HistoryFileState.Unchanged => ("✓ Unchanged since this comparison", "#166534"),
            HistoryFileState.Changed => ("⚠ Changed since this comparison", "#B45309"),
            HistoryFileState.Missing => ("⚠ No longer at this location", "#B45309"),
            _ => ("Could not be checked", "#64748B")
        };
        return new Border
        {
            Background = Brush(background), BorderBrush = Brush(border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush(color) },
                    new TextBlock { Text = file.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap },
                    new SelectableTextBlock { Text = Path.GetDirectoryName(file.Path) ?? "", FontSize = 11, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap },
                    new SelectableTextBlock { Text = $"SHA-256 {file.Sha256 ?? "not recorded"}", FontSize = 10.5, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = stateText, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush(stateColor) }
                }
            }
        };
    }

    private void CompareAgain(HistoryEntry entry)
    {
        if (!File.Exists(entry.Reference.Path) || !File.Exists(entry.Current.Path))
        {
            _status.Text = "One of the files is no longer at its saved location, so it cannot be compared again from here.";
            return;
        }
        _compareAgain(entry);
        Close();
    }

    private async void ExportAll() => await Export("Comparison-history.csv", ComparisonHistory.ToCsv(_entries));

    private async void ExportOne(HistoryEntry entry) => await Export(
        $"Comparison-{entry.ComparedAt.ToLocalTime():yyyy-MM-dd-HHmm}-{Path.GetFileNameWithoutExtension(entry.Current.Name)}.csv",
        ComparisonHistory.FindingsToCsv(entry));

    private async Task Export(string suggestedName, string csv)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save as CSV", SuggestedFileName = suggestedName,
                FileTypeChoices = [new FilePickerFileType("CSV (Excel)") { Patterns = ["*.csv"] }]
            });
            if (file?.TryGetLocalPath() is not { } path)
                return;
            // A byte-order mark lets Excel open the file as UTF-8 (symbols such as ≤ and µ stay correct).
            await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _status.Text = $"Saved {path}";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _status.Text = "The CSV file could not be saved: " + exception.Message;
        }
    }

    private void DeleteOne(HistoryEntry entry)
    {
        _history.Delete(entry.Id);
        Reload();
        _status.Text = "The comparison was removed from the history.";
    }

    private async void ClearAll()
    {
        if (_entries.Count == 0)
            return;
        var confirm = new Window
        {
            Title = "Clear history", Width = 420, Height = 170, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var cleared = false;
        var yes = MakeButton("Delete all", "Delete every saved comparison", () => { cleared = true; confirm.Close(); }, primary: true);
        var no = MakeButton("Cancel", "Keep the history", confirm.Close);
        confirm.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Delete all {_entries.Count} saved comparisons from this computer? This cannot be undone.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
            }
        };
        await confirm.ShowDialog(this);
        if (!cleared)
            return;
        _history.Clear();
        Reload();
        _status.Text = "The history was cleared.";
    }

    private static Border Card(Control child) => new()
    {
        Background = Brushes.White, BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = child
    };

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush("#64748B"), Margin = new Thickness(0, 6, 0, 0) };

    private static Button MakeButton(string text, string tip, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Background = Brush(primary ? "#2457C5" : "#F1F5F9"), Foreground = primary ? Brushes.White : Brush("#172B4D"),
            BorderBrush = Brush(primary ? "#2457C5" : "#E2E8F0")
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

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
