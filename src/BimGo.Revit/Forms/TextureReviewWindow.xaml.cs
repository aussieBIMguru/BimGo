using System.ComponentModel;
using System.Runtime.CompilerServices;
using BimGo.Extraction;
using BimGo.Scene;
using BimGo.Utilities;
using Imaging = System.Windows.Media.Imaging;
using Media = System.Windows.Media;
using Win = System.Windows;
using Wpf = System.Windows.Controls;

// The class belongs to the Forms namespace
namespace BimGo.Forms
{
    /// <summary>
    /// What the "Review textures…" window needs from Revit (run on the Revit thread, behind a progress window).
    /// </summary>
    internal sealed class TextureReviewServices
    {
        /// <summary>Runs the texture review with the given (unsaved) settings.</summary>
        public Func<LaunchSettings, OperationProgress, TextureReview> Review { get; init; }

        /// <summary>Writes the full material report; returns its path and a short summary.</summary>
        public Func<OperationProgress, (string Path, string Summary)> ExportReport { get; init; }

        /// <summary>Revit's main window (progress windows centre on it).</summary>
        public nint Owner { get; init; }
    }

    /// <summary>
    /// "Review textures…" (WPF, modal over the Options window): every material the next extraction will read, with
    /// its texture status, a thumbnail and where its image is. Per material the user can browse to an image, assign a
    /// CC0 proxy, keep it plain, or clear their choice; "Scan a folder…" deep-scans for the missing images one stage
    /// at a time (exact name → other extension → loose name), exact hits pre-ticked, loose ones ticked by hand.
    /// Choices go to the model's override file (<see cref="TextureOverrideSet"/>) on Save; nothing in the Revit model
    /// changes.
    /// </summary>
    public partial class TextureReviewWindow : Win.Window
    {
        #region Fields

        private readonly TextureReview _review;
        private readonly TextureReviewServices _services;
        private readonly LaunchSettings _settings;
        private readonly TextureOverrideSet _overrides;
        private readonly List<ReviewItem> _items;

        // Deep scan state
        private TextureFolderIndex _scanIndex;
        private TextureMatchStage _scanStage;
        private List<string> _scanRemaining = new();
        private List<ScanItem> _scanItems = new();
        private int _scanAccepted;

        #endregion

        /// <summary>
        /// Creates the window.
        /// </summary>
        /// <param name="review">The review pass result.</param>
        /// <param name="services">Revit callbacks (report export).</param>
        /// <param name="settings">The live settings: a remembered search folder is added here (and saved).</param>
        internal TextureReviewWindow(TextureReview review, TextureReviewServices services, LaunchSettings settings)
        {
            _review = review;
            _services = services;
            _settings = settings;
            _overrides = TextureOverrideSet.LoadFromModelFolder(review.ModelFolder, review.HostKey);
            _overrides.ModelTitle = review.ModelTitle;
            _items = review.Rows.Select(r => new ReviewItem(r, _overrides)).ToList();

            InitializeComponent();
            Title = $"BimGo {Globals.ADDIN_VERSION} — Review textures · {review.ModelTitle}";
            try { Icon = UtilRib.GetImageSource("BimGo_Launch", resolution: 32); }
            catch { /* default icon */ }

            ButtonReport.IsEnabled = services?.ExportReport != null;
            TextLocations.Text = DescribeLocations();
            ApplyFilter();
            UpdateSummary();
            UpdateActions();
        }

        #region List

        private string DescribeLocations()
        {
            int search = _settings.TextureSearchFolders?.Count ?? 0;
            return $"Read in {_review.Elapsed.TotalSeconds:F1}s. Looked in: the Autodesk Material Library{(_review.HasLibrary ? "" : " (not installed)")}, " +
                $"{(_review.ExtraPaths == 0 ? "no additional render appearance paths" : $"{_review.ExtraPaths} additional render appearance path{(_review.ExtraPaths == 1 ? "" : "s")}")}, " +
                $"the model folder{(search > 0 ? $" and {search} remembered search folder{(search == 1 ? "" : "s")}" : "")}. " +
                "Unresolved images fall back to the material's shading colour" + (_settings.ProxyMissingTextures ? ", or a CC0 proxy when the name suggests one." : ".");
        }

        private void ApplyFilter()
        {
            IEnumerable<ReviewItem> shown = ChipProxy.IsChecked == true ? _items.Where(i => i.EffectiveProxy != null)
                : ChipAll.IsChecked == true ? _items
                : _items.Where(i => i.IsMissing);
            ListMaterials.ItemsSource = shown.ToList();
        }

        private void UpdateSummary()
        {
            int missing = _items.Count(i => i.IsMissing);
            int overridden = _items.Count(i => i.Override != null);
            TextSummary.Text = $"{_items.Count} material{(_items.Count == 1 ? "" : "s")} · {missing} missing an image · {overridden} with your choice";
            ChipMissing.Content = $"Missing ({missing})";
            ChipProxy.Content = $"Proxy ({_items.Count(i => i.EffectiveProxy != null)})";
            ChipAll.Content = $"All ({_items.Count})";
        }

        private List<ReviewItem> Selected() => ListMaterials.SelectedItems.Cast<ReviewItem>().ToList();

        private void UpdateActions()
        {
            bool any = ListMaterials.SelectedItems.Count > 0;
            ButtonBrowse.IsEnabled = any;
            ButtonProxy.IsEnabled = any;
            ButtonPlain.IsEnabled = any;
            ButtonClear.IsEnabled = any && Selected().Any(i => i.Override != null);
            ButtonScan.IsEnabled = PanelScan.Visibility != Win.Visibility.Visible;
        }

        private void Refresh()
        {
            foreach (ReviewItem item in _items) { item.Changed(); }
            ApplyFilter();
            UpdateSummary();
            UpdateActions();
        }

        private void Filter_Changed(object sender, Win.RoutedEventArgs e)
        {
            if (ListMaterials == null) { return; } // during InitializeComponent
            ApplyFilter();
            UpdateActions();
        }

        private void ListMaterials_SelectionChanged(object sender, Wpf.SelectionChangedEventArgs e) => UpdateActions();

        #endregion

        #region Actions

        private void SetOverride(IEnumerable<ReviewItem> items, Func<ReviewItem, TextureOverride> make)
        {
            foreach (ReviewItem item in items)
            {
                _overrides.Set(item.Row.DocumentKey, item.Row.Material.UniqueId, item.Row.Material.Name, make(item));
            }
            Refresh();
        }

        private void ButtonBrowse_Click(object sender, Win.RoutedEventArgs e)
        {
            List<ReviewItem> selected = Selected();
            if (selected.Count == 0) { return; }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = selected.Count == 1 ? $"Image for “{selected[0].Name}”" : $"Image for {selected.Count} materials",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif|All files|*.*",
                CheckFileExists = true
            };
            string folder = selected.Select(i => i.Row.FoundPath).FirstOrDefault(p => p != null);
            if (folder != null) { dialog.InitialDirectory = Path.GetDirectoryName(folder); }
            if (dialog.ShowDialog(this) != true) { return; }
            SetOverride(selected, i => TextureOverride.ForImage(dialog.FileName, i.Name));
        }

        private void ButtonProxy_Click(object sender, Win.RoutedEventArgs e)
        {
            List<ReviewItem> selected = Selected();
            if (selected.Count == 0) { return; }

            // Suggested keyword(s) of the selection first, then the whole list
            var menu = new Wpf.ContextMenu { PlacementTarget = ButtonProxy, Placement = Wpf.Primitives.PlacementMode.Bottom };
            List<string> suggested = selected.Select(i => i.Row.SuggestedProxy).Where(k => k != null).Distinct().Take(3).ToList();
            foreach (string keyword in suggested)
            {
                menu.Items.Add(ProxyItem(ProxyCatalog.Find(keyword), suggested: true));
            }
            if (suggested.Count > 0) { menu.Items.Add(new Wpf.Separator()); }
            foreach (ProxyKeyword keyword in ProxyCatalog.ALL)
            {
                menu.Items.Add(ProxyItem(keyword, suggested: false));
            }
            menu.IsOpen = true;

            Wpf.MenuItem ProxyItem(ProxyKeyword keyword, bool suggested)
            {
                var item = new Wpf.MenuItem { Header = suggested ? $"{keyword.Label} (suggested)" : keyword.Label, FontWeight = suggested ? Win.FontWeights.SemiBold : Win.FontWeights.Normal };
                item.Click += (_, _) => SetOverride(selected, i => TextureOverride.ForProxy(keyword.Keyword, i.Name));
                return item;
            }
        }

        private void ButtonPlain_Click(object sender, Win.RoutedEventArgs e) =>
            SetOverride(Selected(), i => TextureOverride.ForColourOnly(i.Name));

        private void ButtonClear_Click(object sender, Win.RoutedEventArgs e) =>
            SetOverride(Selected(), _ => null);

        private void ButtonReport_Click(object sender, Win.RoutedEventArgs e)
        {
            if (_services?.ExportReport == null) { return; }
            try
            {
                var progress = new OperationProgress();
                (string path, string summary) result;
                using (ProgressWindow.Show("Writing the material report", progress, _services.Owner))
                {
                    result = _services.ExportReport(progress);
                }
                Win.MessageBoxResult open = Win.MessageBox.Show(this, $"{result.summary}\n\n{result.path}\n\nOpen it now?", "BimGo — Material report",
                    Win.MessageBoxButton.YesNo, Win.MessageBoxImage.Information);
                if (open == Win.MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.path) { UseShellExecute = true })?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                Log_Utils.Write("Material report cancelled.");
            }
            catch (Exception ex)
            {
                Log_Utils.Write($"Material report failed: {ex}");
                Win.MessageBox.Show(this, $"The report could not be written:\n{ex.Message}", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
            }
        }

        private void ButtonSave_Click(object sender, Win.RoutedEventArgs e)
        {
            if (!_overrides.Save())
            {
                Win.MessageBox.Show(this, "Your choices could not be saved (see the log).", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                return;
            }
            Log_Utils.Write($"Texture overrides saved for “{_review.ModelTitle}”: {_overrides.Documents.Values.Sum(d => d.Count)} material(s).");
            DialogResult = true;
        }

        private void ButtonCancel_Click(object sender, Win.RoutedEventArgs e) => DialogResult = false;

        #endregion

        #region Deep scan

        /// <summary>
        /// Picks a folder, indexes its images (progress window, cancellable) and runs the first stage on the materials
        /// whose image is missing and has no image chosen yet.
        /// </summary>
        private void ButtonScan_Click(object sender, Win.RoutedEventArgs e)
        {
            List<string> raws = _items.Where(i => i.IsMissing && i.Row.Material.TextureSource != null)
                .Select(i => i.Row.Material.TextureSource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (raws.Count == 0)
            {
                Win.MessageBox.Show(this, "No material is missing its image.", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder to search for {raws.Count} missing image{(raws.Count == 1 ? "" : "s")}" };
            if (dialog.ShowDialog(this) != true) { return; }
            string folder = dialog.FolderName;

            try
            {
                var progress = new OperationProgress();
                using (ProgressWindow.Show($"Indexing images in {folder}", progress, _services?.Owner ?? 0))
                {
                    progress.Begin("Listing images", 0.0, 1.0);
                    _scanIndex = TextureFolderIndex.Build(folder, progress);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log_Utils.Write($"Texture folder scan failed: {ex}");
                Win.MessageBox.Show(this, $"The folder could not be scanned:\n{ex.Message}", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                return;
            }

            Log_Utils.Write($"Texture deep scan of {folder}: {_scanIndex.FileCount} images in {_scanIndex.FolderCount} folders{(_scanIndex.Truncated ? " (capped)" : "")}.");
            _scanStage = TextureMatchStage.Exact;
            _scanRemaining = raws;
            _scanAccepted = 0;
            CheckRememberFolder.IsChecked = false;
            PanelScan.Visibility = Win.Visibility.Visible;
            RunScanStage();
            UpdateActions();
        }

        private void RunScanStage()
        {
            IReadOnlyList<TextureSearchResult> results = TextureSearch.RunStage(_scanIndex, _scanRemaining, _scanStage);
            _scanItems = results
                .Where(r => r.Stage != TextureMatchStage.None)
                .Select(r => new ScanItem(r, _items.Count(i => i.IsMissing && string.Equals(i.Row.Material.TextureSource, r.Raw, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            _scanRemaining = results.Where(r => r.Stage == TextureMatchStage.None).Select(r => r.Raw).ToList();
            ListScan.ItemsSource = _scanItems;

            string stageName = _scanStage switch
            {
                TextureMatchStage.Exact => "Stage 1 of 3 · exact file names",
                TextureMatchStage.Extension => "Stage 2 of 3 · same name, other extension",
                _ => "Stage 3 of 3 · loose names (separators, case and colour-map suffixes ignored)"
            };
            TextScanHeader.Text = $"{stageName} — {_scanItems.Count} found, {_scanRemaining.Count} still missing · {_scanIndex.Folder} " +
                $"({_scanIndex.FileCount:N0} images{(_scanIndex.Truncated ? ", capped" : "")})";
            TextScanHint.Text = _scanStage == TextureMatchStage.Loose
                ? "Loose matches are proposals: tick the ones that are right. Bump, cutout and reflection maps are never offered."
                : _scanItems.Any(i => i.Result.IsAmbiguous)
                    ? "Exact matches are ticked. Where several files share a name, pick the right one before ticking it."
                    : "Exact matches are ticked. Untick any you don't want.";
            ButtonScanNext.IsEnabled = _scanStage < TextureMatchStage.Loose && _scanRemaining.Count > 0;
        }

        /// <summary>
        /// Turns the ticked scan rows into image overrides for every missing material that uses that path.
        /// </summary>
        private void AcceptTicked()
        {
            foreach (ScanItem scan in _scanItems.Where(s => s.Accepted && s.Selected != null))
            {
                foreach (ReviewItem item in _items.Where(i => i.IsMissing && string.Equals(i.Row.Material.TextureSource, scan.Result.Raw, StringComparison.OrdinalIgnoreCase)))
                {
                    _overrides.Set(item.Row.DocumentKey, item.Row.Material.UniqueId, item.Row.Material.Name, TextureOverride.ForImage(scan.Selected, item.Name));
                    _scanAccepted++;
                }
            }
            Refresh();
        }

        private void ButtonScanNext_Click(object sender, Win.RoutedEventArgs e)
        {
            AcceptTicked();
            _scanStage++;
            RunScanStage();
        }

        private void ButtonScanDone_Click(object sender, Win.RoutedEventArgs e)
        {
            AcceptTicked();
            EndScan(remember: CheckRememberFolder.IsChecked == true);
        }

        private void ButtonScanCancel_Click(object sender, Win.RoutedEventArgs e) => EndScan(remember: false);

        private void EndScan(bool remember)
        {
            if (remember && _scanIndex != null && _settings.AddTextureSearchFolder(_scanIndex.Folder))
            {
                // Saved now as well: the folder is a global choice, kept even if the Options window is cancelled
                LaunchSettings saved = LaunchSettings.LoadOrDefault();
                saved.AddTextureSearchFolder(_scanIndex.Folder);
                saved.Save();
                TextLocations.Text = DescribeLocations();
            }
            if (_scanIndex != null)
            {
                Log_Utils.Write($"Texture deep scan finished: {_scanAccepted} material(s) given an image{(remember ? "; folder remembered" : "")}.");
            }
            PanelScan.Visibility = Win.Visibility.Collapsed;
            _scanIndex = null;
            _scanItems = new List<ScanItem>();
            ListScan.ItemsSource = null;
            UpdateActions();
        }

        #endregion

        #region Row models

        /// <summary>
        /// One material row (bound by the list). Status and thumbnail follow the pending overrides.
        /// </summary>
        internal sealed class ReviewItem : INotifyPropertyChanged
        {
            private static readonly Media.Brush RED = Frozen(0xB4, 0x23, 0x18);
            private static readonly Media.Brush AMBER = Frozen(0x9A, 0x5B, 0x00);
            private static readonly Media.Brush GREEN = Frozen(0x15, 0x80, 0x3D);
            private static readonly Media.Brush BLUE = Frozen(0x0E, 0x74, 0x90);
            private static readonly Media.Brush GREY = Frozen(0x4B, 0x55, 0x60);

            private readonly TextureOverrideSet _overrides;
            private Media.ImageSource _thumbnail;
            private string _thumbnailPath;
            private bool _thumbnailTried;

            public ReviewItem(TextureReviewRow row, TextureOverrideSet overrides)
            {
                Row = row;
                _overrides = overrides;
                Vector3Colour(row.Material.Colour, out byte r, out byte g, out byte b);
                Swatch = Frozen(r, g, b);
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public TextureReviewRow Row { get; }

            /// <summary>The pending override, or null.</summary>
            public TextureOverride Override => _overrides.Find(Row.DocumentKey, Row.Material.UniqueId, Row.Material.Name);

            /// <summary>True if the asset's image is missing and no image has been chosen.</summary>
            public bool IsMissing => Row.Material.TextureState == TextureState.Missing
                && string.IsNullOrWhiteSpace(Override?.Image)
                && Row.Found != TextureFound.Override;

            /// <summary>The proxy that will be drawn (the user's or the automatic one), or null.</summary>
            public string EffectiveProxy
            {
                get
                {
                    TextureOverride choice = Override;
                    if (choice != null) { return string.IsNullOrWhiteSpace(choice.Proxy) ? null : choice.Proxy; }
                    return Row.Found == TextureFound.Override ? null : Row.Material.Proxy;
                }
            }

            public string Name => Row.Material.Name;
            public string Model => Row.ModelLabel;
            public string Detail => $"{ShortSchema(Row.Material.Schema)}{(Row.Uses > 0 ? $" · {Row.Uses} element{(Row.Uses == 1 ? "" : "s")}" : "")}";
            public Media.Brush Swatch { get; }

            public string Source => string.IsNullOrWhiteSpace(Row.Material.TextureSource) ? "(no image: plain colour)" : Row.Material.TextureSource;

            public string Location
            {
                get
                {
                    TextureOverride choice = Override;
                    if (!string.IsNullOrWhiteSpace(choice?.Image)) { return "→ " + choice.Image; }
                    return Row.FoundPath != null ? "→ " + Row.FoundPath : Row.Material.Autodesk ? "Autodesk library image" : string.Empty;
                }
            }

            public string Status
            {
                get
                {
                    TextureOverride choice = Override;
                    if (choice != null)
                    {
                        if (!string.IsNullOrWhiteSpace(choice.Image)) { return File.Exists(choice.Image) ? "Your image" : "Your image (file gone)"; }
                        if (!string.IsNullOrWhiteSpace(choice.Proxy)) { return $"Proxy: {ProxyCatalog.Find(choice.Proxy)?.Label ?? choice.Proxy} (yours)"; }
                        return "Plain colour (yours)";
                    }
                    SceneMaterial m = Row.Material;
                    return m.TextureState switch
                    {
                        TextureState.Embedded when Row.Found == TextureFound.SearchFolder => "Found by search",
                        TextureState.Embedded => $"Found ({FoundLabel(Row.Found)})",
                        TextureState.Missing when m.Proxy != null => $"Missing → proxy: {ProxyCatalog.Find(m.Proxy)?.Label ?? m.Proxy}",
                        TextureState.Missing => "Missing → shading colour",
                        TextureState.Unreadable => "Unreadable image",
                        TextureState.Procedural => "Procedural map → colour",
                        _ => "Plain colour"
                    };
                }
            }

            public Media.Brush StatusBrush
            {
                get
                {
                    TextureOverride choice = Override;
                    if (choice != null) { return BLUE; }
                    return Row.Material.TextureState switch
                    {
                        TextureState.Embedded => GREEN,
                        TextureState.Missing => Row.Material.Proxy != null ? AMBER : RED,
                        TextureState.Unreadable => RED,
                        _ => GREY
                    };
                }
            }

            /// <summary>A 64 px thumbnail of the image that will be used, decoded on first display.</summary>
            public Media.ImageSource Thumbnail
            {
                get
                {
                    string path = !string.IsNullOrWhiteSpace(Override?.Image) ? Override.Image : Row.FoundPath;
                    if (!string.Equals(path, _thumbnailPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _thumbnailPath = path;
                        _thumbnail = null;
                        _thumbnailTried = false;
                    }
                    if (_thumbnailTried || path == null) { return _thumbnail; }
                    _thumbnailTried = true;
                    try
                    {
                        var image = new Imaging.BitmapImage();
                        image.BeginInit();
                        image.UriSource = new Uri(path, UriKind.Absolute);
                        image.DecodePixelWidth = 64;
                        image.CacheOption = Imaging.BitmapCacheOption.OnLoad;
                        image.CreateOptions = Imaging.BitmapCreateOptions.IgnoreColorProfile;
                        image.EndInit();
                        image.Freeze();
                        _thumbnail = image;
                    }
                    catch (Exception ex)
                    {
                        Log_Utils.Write($"Thumbnail of {path} unavailable: {ex.Message}");
                        _thumbnail = null;
                    }
                    return _thumbnail;
                }
            }

            /// <summary>Tells the list every derived value may have changed.</summary>
            public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

            private static string FoundLabel(TextureFound found) => found switch
            {
                TextureFound.Absolute => "path",
                TextureFound.Library => "library",
                TextureFound.ExtraPath => "render path",
                TextureFound.DocumentFolder => "model folder",
                TextureFound.SearchFolder => "search",
                TextureFound.Override => "yours",
                _ => "?"
            };

            private static string ShortSchema(string schema) =>
                string.IsNullOrEmpty(schema) ? "No appearance" : schema.EndsWith("Schema", StringComparison.Ordinal) ? schema[..^6] : schema;

            private static void Vector3Colour(System.Numerics.Vector3 c, out byte r, out byte g, out byte b)
            {
                r = (byte)Math.Clamp((int)Math.Round(c.X * 255f), 0, 255);
                g = (byte)Math.Clamp((int)Math.Round(c.Y * 255f), 0, 255);
                b = (byte)Math.Clamp((int)Math.Round(c.Z * 255f), 0, 255);
            }

            private static Media.Brush Frozen(byte r, byte g, byte b)
            {
                var brush = new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }
        }

        /// <summary>
        /// One deep-scan hit (bound by the scan list).
        /// </summary>
        internal sealed class ScanItem : INotifyPropertyChanged
        {
            private bool _accepted;
            private string _selected;

            public ScanItem(TextureSearchResult result, int materials)
            {
                Result = result;
                MaterialCount = materials;
                _selected = result.Chosen ?? result.Candidates.FirstOrDefault();
                _accepted = result.PreTicked;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public TextureSearchResult Result { get; }
            public int MaterialCount { get; }
            public string RawName => TextureSearch.FileNamesOf(Result.Raw).FirstOrDefault() ?? Result.Raw;
            public IReadOnlyList<string> Candidates => Result.Candidates;
            public bool HasCandidate => Result.Candidates.Count > 0;

            public string StageLabel => Result.Stage switch
            {
                TextureMatchStage.Exact => Result.IsAmbiguous ? $"exact ×{Result.Candidates.Count}" : "exact",
                TextureMatchStage.Extension => Result.IsAmbiguous ? $"ext ×{Result.Candidates.Count}" : "extension",
                TextureMatchStage.Loose => "loose",
                _ => string.Empty
            };

            public bool Accepted
            {
                get => _accepted;
                set { _accepted = value; Notify(); }
            }

            public string Selected
            {
                get => _selected;
                set
                {
                    _selected = value;
                    Notify();
                    // Picking one of several same-named files is a decision: tick it
                    if (Result.IsAmbiguous && value != null && !_accepted) { Accepted = true; }
                }
            }

            private void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        #endregion
    }
}
