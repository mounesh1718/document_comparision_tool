using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DocumentChecker.Engine.Sources;

namespace DocumentChecker.Engine;

/// <summary>
/// Source folders for Source check: create a folder, upload a folder of source PDFs into it (same structure, every copy
/// verified), see its files as a tree with how each was read, bring it up to date from the originals, or delete it.
/// </summary>
internal sealed class SourceLibraryWindow : Window
{
    private readonly string _root;
    private readonly ListBox _libraries = new() { Background = Brushes.Transparent };
    private readonly TextBlock _libraryCount = new() { Foreground = Brush("#64748B"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { FontSize = 12, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap };
    private readonly TreeView _tree = new() { Background = Brushes.Transparent };
    private readonly StackPanel _fileDetails = new() { Spacing = 4 };
    private readonly TextBlock _empty = new()
    {
        Text = "Create a source folder with \"New source folder\", then upload your folder of source PDFs into it.",
        FontSize = 14, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 30)
    };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _status = new() { Foreground = Brush("#475569"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly List<Button> _libraryButtons = [];
    private readonly Button _stop;
    private Grid _detailsGrid = null!;
    private CancellationTokenSource? _working;

    public SourceLibraryWindow(string? root = null)
    {
        _root = root ?? SourceLibrary.DefaultRoot;
        Title = "Source folders";
        Width = 1180;
        Height = 780;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F3F5F9");
        FontFamily = new FontFamily("Segoe UI, Helvetica Neue, Arial");
        _stop = MakeButton("Stop", "Stop the upload. What is already copied and verified is kept.", () => _working?.Cancel());
        _stop.IsVisible = false;
        _libraries.ItemTemplate = new FuncDataTemplate<SourceLibrary>((library, _) => library is null ? new TextBlock() : LibraryItem(library), supportsRecycling: false);
        _libraries.SelectionChanged += (_, _) => ShowLibrary();
        _tree.SelectionChanged += (_, _) => ShowFile((_tree.SelectedItem as TreeViewItem)?.Tag as SourceFile);
        Content = BuildLayout();
        Reload();
        KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Escape && _working is null)
                Close();
        };
        Closing += (_, _) => _working?.Cancel();
    }

    private SourceLibrary? Selected => _libraries.SelectedItem as SourceLibrary;

    // ---- for tests ----

    /// <summary>The source folders listed.</summary>
    internal int ListedCount => _libraries.ItemCount;

    /// <summary>The tree as text, one line per folder or file, indented by two spaces per level.</summary>
    internal string TreeText => string.Join("\n", Lines(_tree.Items.OfType<TreeViewItem>(), 0));

    internal string StatusText => _status.Text ?? "";

    internal string SummaryText => _summary.Text ?? "";

    internal string FileDetailsText => string.Join("\n", _fileDetails.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    /// <summary>Creates a source folder as the "New source folder" dialog does; the error message, or null.</summary>
    internal string? CreateLibrary(string name)
    {
        try
        {
            var library = SourceLibrary.Create(name, _root);
            Reload(library.Folder);
            _status.Text = $"Source folder \"{library.Name}\" created. Now upload your folder of source PDFs into it.";
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    /// <summary>Uploads a folder into the selected source folder, as "Upload folder…" does after the folder is chosen.</summary>
    internal Task UploadFolderAsync(string folder) =>
        RunAsync($"Uploading {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}", (library, progress, cancel) =>
            library.UploadFolder(folder, null, progress, cancel).Summary);

    /// <summary>Selects the file with this path in the tree, as a click would.</summary>
    internal void SelectFile(string relativePath) =>
        _tree.SelectedItem = AllItems(_tree.Items.OfType<TreeViewItem>()).FirstOrDefault(item => (item.Tag as SourceFile)?.RelativePath == relativePath);

    // ---- layout ----

    private Control BuildLayout()
    {
        var top = Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(16, 12),
            Children =
            {
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = "Source folders", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") },
                        new TextBlock
                        {
                            Text = $"Your source PDFs for Source check. Copies are kept on this computer in {_root}. Your original files are only read, never changed.",
                            FontSize = 12, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                Column(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        Track(MakeButton("New source folder", "Create a new, empty source folder", NewLibrary, primary: true)),
                        MakeButton("Close", "Close (Esc)", Close)
                    }
                }, 1)
            }
        });

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
                    Children = { new TextBlock { Text = "Source folders", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") }, Column(_libraryCount, 1) }
                },
                Row(_libraries, 1)
            }
        });

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Spaced(Track(MakeButton("Upload folder…", "Upload a folder with all its subfolders. The same structure is kept, and every PDF copy is checked against the original.", UploadFolder, primary: true))),
                Spaced(Track(MakeButton("Add PDF files…", "Add single PDF files at the top of this source folder", AddFiles))),
                Spaced(Track(MakeButton("Update from originals", "Upload every folder again from where it came from: new and changed PDFs are copied, removed ones are removed", UpdateFromOriginals))),
                Spaced(Track(MakeButton("Delete source folder", "Delete this source folder and its copies from this computer (your originals are not touched)", DeleteLibrary))),
                Spaced(_stop)
            }
        };
        var treeCard = new Border
        {
            BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            Child = _tree
        };
        var fileCard = new Border
        {
            BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10),
            Background = Brush("#F8FAFC"),
            Child = new ScrollViewer { Content = _fileDetails, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        _detailsGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("*,320"),
            RowSpacing = 8,
            ColumnSpacing = 10,
            Margin = new Thickness(16, 12)
        };
        _detailsGrid.Children.Add(Span(_title, 0));
        _detailsGrid.Children.Add(Span(_summary, 1));
        _detailsGrid.Children.Add(Span(actions, 2));
        _detailsGrid.Children.Add(Span(_progress, 3));
        _detailsGrid.Children.Add(Row(treeCard, 4));
        _detailsGrid.Children.Add(Row(Column(fileCard, 1), 4));
        var detailsCard = Card(new Panel { Children = { _detailsGrid, _empty } });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), ColumnSpacing = 10 };
        body.Children.Add(listCard);
        body.Children.Add(Column(detailsCard, 1));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(Row(body, 1));
        root.Children.Add(Row(_status, 2));
        return root;
    }

    private static Control LibraryItem(SourceLibrary library)
    {
        var readable = library.Files.Where(file => file.IsReadable).ToList();
        var problems = library.Files.Count - readable.Count;
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 6) };
        panel.Children.Add(new TextBlock { Text = library.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = library.Files.Count == 0 ? "Empty" : $"{Count(library.Files.Count, "PDF")} · {Count(readable.Sum(file => file.Pages), "page")}",
            FontSize = 12, Foreground = Brush("#64748B")
        });
        if (problems > 0)
            panel.Children.Add(new TextBlock { Text = $"⚠ {problems} cannot be read", FontSize = 12, Foreground = Brush("#B45309") });
        return panel;
    }

    private void Reload(string? select = null)
    {
        var keep = select ?? Selected?.Folder;
        var libraries = SourceLibrary.List(_root);
        _libraries.ItemsSource = libraries;
        _libraryCount.Text = libraries.Count == 0 ? "" : libraries.Count.ToString(CultureInfo.CurrentCulture);
        _libraries.SelectedItem = libraries.FirstOrDefault(library => library.Folder == keep) ?? libraries.FirstOrDefault();
        ShowLibrary();
    }

    private void ShowLibrary()
    {
        var library = Selected;
        _detailsGrid.IsVisible = library is not null;
        _empty.IsVisible = library is null;
        _tree.Items.Clear();
        ShowFile(null);
        if (library is null)
            return;

        _title.Text = library.Name;
        var readable = library.Files.Where(file => file.IsReadable).ToList();
        var ocr = readable.Sum(file => file.OcrPages);
        var notRead = readable.Sum(file => file.ScannedPagesNotRead);
        var problems = library.Files.Count - readable.Count;
        _summary.Text = library.Files.Count == 0
            ? $"Created {library.CreatedAt.ToLocalTime():d MMM yyyy}. No PDFs yet: click \"Upload folder…\" and choose your folder of source PDFs."
            : string.Join(" · ", new[]
                {
                    $"{Count(library.Files.Count, "PDF")}, {Count(readable.Sum(file => file.Pages), "page")}",
                    ocr > 0 ? $"{Count(ocr, "scanned page")} read by text recognition" : null,
                    notRead > 0 ? $"⚠ {Count(notRead, "scanned page")} could not be read" : null,
                    problems > 0 ? $"⚠ {problems} PDF{(problems == 1 ? "" : "s")} cannot be read" : null,
                    library.Uploads.Count > 0 ? "uploaded from " + string.Join(", ", library.Uploads.Select(upload => upload.OriginalPath)) : null
                }.OfType<string>());
        foreach (var node in Nodes(library.Files, ""))
            _tree.Items.Add(node);
    }

    /// <summary>The tree of folders and files under <paramref name="prefix"/>: folders first, then files, each by name.</summary>
    private static IEnumerable<TreeViewItem> Nodes(IReadOnlyList<SourceFile> files, string prefix)
    {
        var here = files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var folders = here.Select(file => file.RelativePath[prefix.Length..])
            .Where(rest => rest.Contains('/'))
            .Select(rest => rest[..rest.IndexOf('/')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var inside = here.Count(file => file.RelativePath.StartsWith(prefix + folder + "/", StringComparison.OrdinalIgnoreCase));
            var item = new TreeViewItem
            {
                IsExpanded = true,
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = folder, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D") },
                        new TextBlock { Text = Count(inside, "PDF"), FontSize = 11.5, Foreground = Brush("#94A3B8"), VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            foreach (var child in Nodes(files, prefix + folder + "/"))
                item.Items.Add(child);
            yield return item;
        }
        foreach (var file in here.Where(file => !file.RelativePath[prefix.Length..].Contains('/')).OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (state, color) = StateOf(file);
            yield return new TreeViewItem
            {
                Tag = file,
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = file.Name, Foreground = Brush("#172B4D") },
                        new TextBlock { Text = state, FontSize = 11.5, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
        }
    }

    private static (string Text, string Color) StateOf(SourceFile file) =>
        file.Problem is { } problem ? ("⚠ " + problem, "#B45309")
        : file.ScannedPagesNotRead > 0 ? ($"{Count(file.Pages, "page")} · ⚠ {Count(file.ScannedPagesNotRead, "scanned page")} not read", "#B45309")
        : file.OcrPages > 0 ? ($"{Count(file.Pages, "page")} · {file.OcrPages} read by text recognition", "#2457C5")
        : ($"{Count(file.Pages, "page")}", "#64748B");

    private void ShowFile(SourceFile? file)
    {
        _fileDetails.Children.Clear();
        if (file is null || Selected is not { } library)
        {
            _fileDetails.Children.Add(new TextBlock
            {
                Text = "Click a PDF to see where it came from, its fingerprint and how its text was read.",
                FontSize = 12.5, Foreground = Brush("#64748B"), TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        var (state, color) = StateOf(file);
        _fileDetails.Children.Add(new TextBlock { Text = file.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brush("#172B4D"), TextWrapping = TextWrapping.Wrap });
        _fileDetails.Children.Add(new TextBlock { Text = state, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap });
        AddField("IN THIS SOURCE FOLDER", file.Folder.Length == 0 ? "(top level)" : file.Folder);
        AddField("ORIGINAL", file.OriginalPath ?? "not recorded");
        AddField("UPLOADED", $"{file.UploadedAt.ToLocalTime():d MMM yyyy, HH:mm} · {file.Size / 1024.0:N0} KB · copy verified");
        AddField("SHA-256", file.Sha256);
        var remove = MakeButton("Remove from source folder", "Remove this PDF's copy (the original is not touched)", () => RemoveFile(library, file));
        remove.Margin = new Thickness(0, 10, 0, 0);
        _fileDetails.Children.Add(remove);

        void AddField(string caption, string value)
        {
            _fileDetails.Children.Add(new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush("#64748B"), Margin = new Thickness(0, 6, 0, 0) });
            _fileDetails.Children.Add(new SelectableTextBlock { Text = value, FontSize = 12, Foreground = Brush("#334155"), TextWrapping = TextWrapping.Wrap });
        }
    }

    // ---- actions ----

    private async void NewLibrary()
    {
        var dialog = new Window
        {
            Title = "New source folder", Width = 440, Height = 210, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var name = new TextBox { Watermark = "For example: Examplin 250 mg – Module 3" };
        var error = new TextBlock { Foreground = Brush("#B91C1C"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void Create()
        {
            if (CreateLibrary(name.Text ?? "") is { } message)
                error.Text = message;
            else
                dialog.Close();
        }
        name.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
                Create();
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Name of the new source folder", FontWeight = FontWeight.SemiBold },
                name,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { MakeButton("Cancel", "Do not create a folder", dialog.Close), MakeButton("Create", "Create the source folder", Create, primary: true) }
                }
            }
        };
        dialog.Opened += (_, _) => name.Focus();
        await dialog.ShowDialog(this);
    }

    private async void UploadFolder()
    {
        if (Selected is null)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Choose the folder of source PDFs to upload into \"{Selected.Name}\"", AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await UploadFolderAsync(path);
    }

    private async void AddFiles()
    {
        if (Selected is null)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose source PDFs to add to \"{Selected.Name}\"", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await RunAsync("Adding PDF files", (library, progress, cancel) => library.AddFiles(paths, null, progress, cancel).Summary);
    }

    private async void UpdateFromOriginals()
    {
        if (Selected is not { } selected)
            return;
        if (selected.Uploads.Count == 0)
        {
            _status.Text = "No folder has been uploaded into this source folder yet, so there is nothing to update.";
            return;
        }
        await RunAsync("Updating from the originals", (library, progress, cancel) => string.Join(" ",
            library.UpdateFromOriginals(null, progress, cancel).Select(result => result.Report is { } report
                ? $"{result.Upload.Name}: {report.Summary}"
                : $"{result.Upload.Name}: ⚠ the original folder {result.Upload.OriginalPath} was not found, so it was not updated.")));
    }

    private async void DeleteLibrary()
    {
        if (Selected is not { } library)
            return;
        var confirm = new Window
        {
            Title = "Delete source folder", Width = 460, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var delete = false;
        confirm.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = $"Delete \"{library.Name}\" and its {Count(library.Files.Count, "PDF copy", "PDF copies")} from this computer? Your original files are not touched.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        MakeButton("Cancel", "Keep the source folder", confirm.Close),
                        MakeButton("Delete", "Delete the source folder", () => { delete = true; confirm.Close(); }, primary: true)
                    }
                }
            }
        };
        await confirm.ShowDialog(this);
        if (!delete)
            return;
        try
        {
            library.Delete();
            _status.Text = $"\"{library.Name}\" was deleted.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status.Text = "The source folder could not be deleted completely: " + exception.Message;
        }
        Reload();
    }

    private void RemoveFile(SourceLibrary library, SourceFile file)
    {
        library.Remove(file.RelativePath);
        _status.Text = $"{file.Name} was removed from \"{library.Name}\". The original is not touched.";
        Reload(library.Folder);
    }

    /// <summary>Runs an upload in the background with progress and a Stop button; the buttons wait until it is done.</summary>
    private async Task RunAsync(string what, Func<SourceLibrary, IProgress<SourceProgress>, CancellationToken, string> work)
    {
        if (Selected is not { } library || _working is not null)
            return;
        _working = new CancellationTokenSource();
        foreach (var button in _libraryButtons)
            button.IsEnabled = false;
        _libraries.IsEnabled = _tree.IsEnabled = _fileDetails.IsEnabled = false;
        _stop.IsVisible = _progress.IsVisible = true;
        _progress.Value = 0;
        _status.Text = what + "…";
        var progress = new Progress<SourceProgress>(step =>
        {
            _progress.Value = step.Total == 0 ? 1 : (double)step.Done / step.Total;
            _status.Text = step.Current.Length == 0 ? what + "…" : $"{what}: {step.Done + 1} of {step.Total} · {step.Current}";
        });
        var token = _working.Token;
        try
        {
            var summary = await Task.Run(() => work(library, progress, token));
            // The last progress report can arrive after the work is done; the summary is shown after it.
            await Task.Yield();
            _status.Text = summary;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = "The upload could not be completed: " + exception.Message;
        }
        finally
        {
            _working.Dispose();
            _working = null;
            foreach (var button in _libraryButtons)
                button.IsEnabled = true;
            _libraries.IsEnabled = _tree.IsEnabled = _fileDetails.IsEnabled = true;
            _stop.IsVisible = _progress.IsVisible = false;
            var status = _status.Text;
            Reload(library.Folder);
            _status.Text = status;
        }
    }

    // ---- helpers ----

    private Button Track(Button button)
    {
        _libraryButtons.Add(button);
        return button;
    }

    private static IEnumerable<string> Lines(IEnumerable<TreeViewItem> items, int depth)
    {
        foreach (var item in items)
        {
            var header = string.Join(" · ", ((Panel)item.Header!).Children.OfType<TextBlock>().Select(text => text.Text));
            yield return new string(' ', depth * 2) + header;
            foreach (var line in Lines(item.Items.OfType<TreeViewItem>(), depth + 1))
                yield return line;
        }
    }

    private static IEnumerable<TreeViewItem> AllItems(IEnumerable<TreeViewItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(AllItems(item.Items.OfType<TreeViewItem>())));

    private static string Count(int count, string one, string? many = null) =>
        $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? one : many ?? one + "s")}";

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
            BorderBrush = Brush(primary ? "#2457C5" : "#E2E8F0")
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Control Spaced(Control control)
    {
        control.Margin = new Thickness(0, 0, 8, 0);
        return control;
    }

    private static Control Span(Control control, int row)
    {
        Grid.SetRow(control, row);
        Grid.SetColumnSpan(control, 2);
        return control;
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
