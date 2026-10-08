using System.Globalization;
using BimGo.Scene;
using Media = System.Windows.Media;
using Win = System.Windows;
using Wpf = System.Windows.Controls;

// The class belongs to the Forms namespace
namespace BimGo.Forms
{
    /// <summary>
    /// The modal launch-options dialog (WPF), in seven tabs (Load · Categories · Geometry · Materials · Links · Parameters
    /// · Player; the last one used is remembered) with the start position and the Launch button always visible. Edits a
    /// <see cref="LaunchSettings"/> in place.
    ///
    /// Note: the project enables both WPF and WinForms, so WinForms types are in the global usings.
    /// WPF types are referenced through the Wpf/Win/Media aliases to avoid ambiguous names
    /// (CheckBox, Orientation, Brushes...).
    /// </summary>
    public partial class OptionsWindow : Win.Window
    {
        #region Fields

        private static readonly string[] GROUP_DESCRIPTIONS =
        {
            "Walls, floors, ceilings, roofs, doors, windows, curtain walls, stairs, structure",
            "Casework, furniture, generic models, plumbing fixtures, railings, specialty",
            "Lighting, electrical, mechanical, fire alarm, security, sprinklers"
        };

        private readonly LaunchSettings _settings;
        private readonly int[] _counts;
        private readonly Wpf.CheckBox[] _groupChecks = new Wpf.CheckBox[3];
        private readonly Dictionary<string, Wpf.CheckBox> _categoryChecks = new();
        private bool _updating;

        // Extra parameters: every known name (from a scan and the saved settings) and the picked ones
        private readonly Func<List<string>> _scanParameterNames;
        private readonly SortedSet<string> _knownParameters = new(StringComparer.CurrentCultureIgnoreCase);
        private readonly HashSet<string> _pickedParameters = new(StringComparer.Ordinal);
        private bool _scanned;

        // Phases: the model's phase names (in order) and the defaults used when nothing is saved
        private readonly PhaseChoices _phases;
        private const string NO_PHASE = "(none)";

        // Linked models: the host's link instances and their tick boxes (by instance UniqueId)
        private readonly LinkChoices _links;

        // The active view (for "only elements visible in the active view")
        private readonly ViewChoice _view;
        private readonly Dictionary<string, Wpf.CheckBox> _linkChecks = new(StringComparer.Ordinal);

        // Materials: the texture review (null where the caller offers none, e.g. no Revit document)
        private readonly TextureReviewServices _textureServices;

        // Quality profile: a working copy of the settings a profile governs. The visible controls (colour, anti-aliasing,
        // shadow quality, lights) are read into it; the walkthrough-only values a profile sets (AO, bloom, reflections)
        // live only here and are written back on Launch.
        private LaunchSettings _display;

        #endregion

        /// <summary>
        /// Creates the dialog.
        /// </summary>
        /// <param name="settings">Settings to edit (written back on Launch).</param>
        /// <param name="spawnDescription">Where the player will start.</param>
        /// <param name="counts">Element count per catalog definition.</param>
        /// <param name="scanParameterNames">Lists the model's parameter names (runs on the Revit thread), or null.</param>
        /// <param name="primaryButtonText">The confirm button's text ("Launch BimGo", "Export…").</param>
        /// <param name="phases">The model's phases and their defaults.</param>
        /// <param name="links">The model's link instances and the key the choice is saved under.</param>
        /// <param name="view">The active view (name and how many elements it shows).</param>
        /// <param name="textureServices">The "Review textures…" callbacks, or null to hide the button.</param>
        internal OptionsWindow(LaunchSettings settings, string spawnDescription, int[] counts,
            Func<List<string>> scanParameterNames, string primaryButtonText, PhaseChoices phases, LinkChoices links, ViewChoice view,
            TextureReviewServices textureServices = null)
        {
            _textureServices = textureServices;
            _settings = settings;
            _counts = counts;
            _scanParameterNames = scanParameterNames;
            _phases = phases ?? new PhaseChoices();
            _links = links ?? new LinkChoices();
            _view = view ?? new ViewChoice();
            _display = settings.Clone();

            InitializeComponent();
            Title = $"BimGo {Globals.ADDIN_VERSION} — Options";

            // The BimGo ">>" icon (same image as the Go button)
            try { Icon = UtilRib.GetImageSource("BimGo_Launch", resolution: 32); }
            catch { /* default icon */ }

            RunSpawn.Text = spawnDescription;
            if (!string.IsNullOrEmpty(primaryButtonText)) { ButtonLaunch.Content = primaryButtonText; }
            ButtonScanParameters.IsEnabled = scanParameterNames != null;
            BuildCategoryCards();
            BuildLinkList();
            LoadFromSettings();
            UpdateEstimate();
        }

        #region Build

        /// <summary>
        /// Builds one card per group with a group tick box and an expandable per-category list.
        /// </summary>
        private void BuildCategoryCards()
        {
            for (int g = 0; g < 3; g++)
            {
                var group = (CategoryGroup)g;
                List<CategoryDef> defs = CategoryCatalog.All.Where(d => d.Group == group && IsOffered(d)).ToList();

                var card = new Wpf.Border
                {
                    Style = (Win.Style)FindResource("Card"),
                    Margin = new Win.Thickness(5, 0, 5, 0)
                };
                var stack = new Wpf.StackPanel();
                card.Child = stack;

                var groupCheck = new Wpf.CheckBox
                {
                    Content = CategoryCatalog.GROUP_NAMES[g],
                    FontWeight = Win.FontWeights.SemiBold,
                    Tag = group
                };
                groupCheck.Click += GroupCheck_Click;
                _groupChecks[g] = groupCheck;
                stack.Children.Add(groupCheck);

                stack.Children.Add(new Wpf.TextBlock
                {
                    Text = GROUP_DESCRIPTIONS[g],
                    TextWrapping = Win.TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = (Media.Brush)FindResource("Muted"),
                    Margin = new Win.Thickness(0, 6, 0, 6)
                });

                var list = new Wpf.StackPanel { Margin = new Win.Thickness(0, 4, 0, 0) };
                foreach (CategoryDef def in defs)
                {
                    var check = new Wpf.CheckBox
                    {
                        Content = $"{def.Label} ({_counts[def.Index]:N0})",
                        Tag = def.Key,
                        Margin = new Win.Thickness(0, 2, 0, 2),
                        FontSize = 12
                    };
                    check.Checked += AnyCategory_Changed;
                    check.Unchecked += AnyCategory_Changed;
                    _categoryChecks[def.Key] = check;
                    list.Children.Add(check);
                }

                stack.Children.Add(new Wpf.Expander
                {
                    Header = $"Edit categories ({defs.Count})",
                    Foreground = (Media.Brush)FindResource("Accent"),
                    FontSize = 12,
                    Content = list
                });

                GridGroups.Children.Add(card);
            }

            // Heavy categories have no individual tick boxes; CheckHeavy drives them all
            int heavyCount = CategoryCatalog.All.Where(d => d.Heavy).Sum(d => _counts[d.Index]);
            CheckHeavy.Content = $"Include ducts, pipes, cable trays and conduits (heavy, {heavyCount:N0})";
        }

        /// <summary>
        /// Pushes settings into the controls.
        /// </summary>
        private void LoadFromSettings()
        {
            _updating = true;

            var enabled = new HashSet<string>(_settings.EnabledCategories);
            foreach (KeyValuePair<string, Wpf.CheckBox> pair in _categoryChecks)
            {
                pair.Value.IsChecked = enabled.Contains(pair.Key);
            }
            CheckHeavy.IsChecked = CategoryCatalog.All.Any(d => d.Heavy && enabled.Contains(d.Key));

            TextThreshold.Text = _settings.TriangleThreshold.ToString(CultureInfo.InvariantCulture);
            RadioProxy.IsChecked = _settings.OverLimit == OverLimitMode.Proxy;
            RadioSkip.IsChecked = _settings.OverLimit == OverLimitMode.Skip;
            RadioWhitecard.IsChecked = _settings.Colour == ColourMode.Whitecard;
            RadioMaterial.IsChecked = _settings.Colour == ColourMode.Material;
            RadioRealistic.IsChecked = _settings.Colour == ColourMode.Realistic;
            LoadTextures();
            TextStep.Text = _settings.MaxStepHeightMm.ToString("0", CultureInfo.InvariantCulture);

            ComboMsaa.SelectedIndex = _settings.Msaa >= 4 ? 2 : _settings.Msaa >= 2 ? 1 : 0;
            SliderFov.Value = _settings.FieldOfView;
            SliderSensitivity.Value = _settings.MouseSensitivity;
            CheckInvertY.IsChecked = _settings.InvertY;
            CheckVSync.IsChecked = _settings.VSync;
            CheckComments.IsChecked = _settings.LoadComments;
            LoadSnap();
            ComboShadowQuality.SelectedIndex = Math.Clamp((int)_settings.ShadowQuality, 0, 2);
            ComboArtificialLights.SelectedIndex = Math.Clamp((int)_settings.ArtificialLights, 0, 2);
            CheckViewOnly.IsChecked = _settings.ActiveViewOnly && _view.Available;
            CheckViewOnly.IsEnabled = _view.Available;
            CheckSkipHelpers.IsChecked = _settings.SkipHelperGeometry;
            TextHelperKeywords.Text = string.Join(", ", _settings.HelperSubcategoryKeywords ?? LaunchSettings.DefaultHelperKeywords());
            LoadPhases();
            CheckSidecarsBesideModel.IsChecked = _settings.SidecarsBesideModel;
            Tabs.SelectedIndex = Math.Clamp(_settings.LastOptionsTab, 0, Tabs.Items.Count - 1);

            foreach (string name in _settings.ExtraParameters ?? new List<string>())
            {
                _pickedParameters.Add(name);
                _knownParameters.Add(name);
            }
            RebuildParameterList();

            _updating = false;
            RefreshGroupChecks();
            UpdateSliderLabels();
            UpdateViewOnly();
            RefreshProfile();
        }

        #endregion

        #region Events

        /// <summary>
        /// A group tick box sets or clears all of its categories.
        /// </summary>
        private void GroupCheck_Click(object sender, Win.RoutedEventArgs e)
        {
            if (sender is not Wpf.CheckBox groupCheck || groupCheck.Tag is not CategoryGroup group) { return; }

            // Clicking an indeterminate box resolves to "all on"
            bool on = groupCheck.IsChecked != false;
            _updating = true;
            foreach (CategoryDef def in CategoryCatalog.All.Where(d => d.Group == group && IsOffered(d)))
            {
                _categoryChecks[def.Key].IsChecked = on;
            }
            _updating = false;

            RefreshGroupChecks();
            UpdateEstimate();
        }

        /// <summary>
        /// Any category tick box changed.
        /// </summary>
        private void AnyCategory_Changed(object sender, Win.RoutedEventArgs e)
        {
            if (_updating) { return; }
            RefreshGroupChecks();
            UpdateEstimate();
        }

        /// <summary>
        /// Slider changed (fires during InitializeComponent too, hence the null guard).
        /// </summary>
        private void Slider_ValueChanged(object sender, Win.RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateSliderLabels();
        }

        /// <summary>
        /// Materials and textures: the tick, the size cap and where textures will be found on this machine.
        /// </summary>
        private void LoadTextures()
        {
            CheckTextures.IsChecked = _settings.ExtractTextures;
            CheckProxyTextures.IsChecked = _settings.ProxyMissingTextures;
            ButtonReviewTextures.Visibility = _textureServices?.Review != null ? Win.Visibility.Visible : Win.Visibility.Collapsed;
            UpdateOverrideSummary();
            ComboTextureSize.Items.Clear();
            foreach (int size in MaterialData.TEXTURE_SIZES) { ComboTextureSize.Items.Add($"{size} px"); }
            ComboTextureSize.SelectedIndex = Math.Max(0, Array.IndexOf(MaterialData.TEXTURE_SIZES, MaterialData.NearestTextureSize(_settings.TextureMaxSize)));

            LoadTextureSourcesText();
            UpdateTextureControls();
        }

        /// <summary>
        /// Where textures will be found on this machine (one line under the tick).
        /// </summary>
        private void LoadTextureSourcesText()
        {
            try
            {
                Extraction.TextureLocator locator = Extraction.TextureLocator.Discover(Globals.REVIT_VERSION_STR);
                string library = locator.HasLibrary ? "Autodesk Material Library found" : "Autodesk Material Library not installed (library textures will be missing)";
                string extra = locator.ExtraPaths.Count == 0
                    ? "no additional render appearance paths set in Revit Options"
                    : $"{locator.ExtraPaths.Count} additional render appearance path{(locator.ExtraPaths.Count == 1 ? "" : "s")}: {string.Join("; ", locator.ExtraPaths)}";
                int search = _settings.TextureSearchFolders?.Count ?? 0;
                string folders = search == 0 ? string.Empty : $"; {search} remembered texture search folder{(search == 1 ? "" : "s")}";
                TextTextureSources.Text = $"{library}; {extra}{folders}.";
            }
            catch (Exception ex)
            {
                TextTextureSources.Text = "Texture folders could not be checked (see the log).";
                Utilities.Log_Utils.Write($"Texture folder check failed: {ex.Message}");
            }
        }

        private void UpdateTextureControls()
        {
            bool on = CheckTextures.IsChecked == true;
            ComboTextureSize.IsEnabled = on;
            CheckProxyTextures.IsEnabled = on;
            ButtonReviewTextures.IsEnabled = on;
        }

        /// <summary>
        /// "n materials with your texture choice" for this model (from its override file).
        /// </summary>
        private void UpdateOverrideSummary()
        {
            try
            {
                TextureOverrideSet overrides = TextureOverrideSet.LoadFromModelFolder(_links.ModelFolder, _links.HostKey);
                int count = overrides.Documents.Values.Sum(d => d.Count);
                TextTextureOverrides.Text = count == 0 ? string.Empty : $"{count} material{(count == 1 ? "" : "s")} with your texture choice in this model";
            }
            catch (Exception ex)
            {
                TextTextureOverrides.Text = string.Empty;
                Utilities.Log_Utils.Write($"Texture overrides unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs the texture review with the choices as they stand in this window (categories, active view, links,
        /// phases), then opens the review window.
        /// </summary>
        private void ButtonReviewTextures_Click(object sender, Win.RoutedEventArgs e)
        {
            if (_textureServices?.Review == null) { return; }
            if (!ReadPhases(out string existingPhase, out string newPhase)) { return; }

            LaunchSettings trial = _settings.Clone();
            List<string> enabled = SelectedKeys();
            bool viewOnly = CheckViewOnly.IsChecked == true;
            if (enabled.Count == 0 && !viewOnly)
            {
                Win.MessageBox.Show(this, "Tick at least one category to load (or load only what the active view shows).", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                return;
            }
            if (enabled.Count > 0) { trial.EnabledCategories = enabled; }
            trial.ActiveViewOnly = viewOnly;
            trial.ExistingPhase = existingPhase;
            trial.NewPhase = newPhase;
            trial.ExtractTextures = true;
            trial.ProxyMissingTextures = CheckProxyTextures.IsChecked == true;
            trial.SetLinksFor(_links.HostKey, SelectedLinks());

            Extraction.TextureReview review;
            try
            {
                var progress = new Utilities.OperationProgress();
                using (ProgressWindow.Show("Reviewing textures", progress, _textureServices.Owner))
                {
                    review = _textureServices.Review(trial, progress);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Texture review failed: {ex}");
                Win.MessageBox.Show(this, $"The texture review failed:\n{ex.Message}", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                return;
            }

            if (review.Rows.Count == 0)
            {
                Win.MessageBox.Show(this, "None of the elements this Go would load carry a material.", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Information);
                return;
            }

            var window = new TextureReviewWindow(review, _textureServices, _settings) { Owner = this };
            window.ShowDialog();
            UpdateOverrideSummary();
            LoadTextureSourcesText();
        }

        /// <summary>
        /// Realistic colours need textures: ticking it ticks the extraction.
        /// </summary>
        private void RadioRealistic_Click(object sender, Win.RoutedEventArgs e)
        {
            if (RadioRealistic.IsChecked == true) { CheckTextures.IsChecked = true; }
            UpdateTextureControls();
            RefreshProfile();
        }

        /// <summary>
        /// Without textures the Realistic mode has nothing to show: unticking falls back to material colours.
        /// </summary>
        private void CheckTextures_Click(object sender, Win.RoutedEventArgs e)
        {
            if (CheckTextures.IsChecked != true && RadioRealistic.IsChecked == true) { RadioMaterial.IsChecked = true; }
            UpdateTextureControls();
            RefreshProfile();
        }

        /// <summary>
        /// Validates and writes the settings back, then closes.
        /// </summary>
        private void ButtonLaunch_Click(object sender, Win.RoutedEventArgs e)
        {
            if (!int.TryParse(TextThreshold.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int threshold) || threshold < 100)
            {
                ShowTabOf(TextThreshold);
                Win.MessageBox.Show(this, "The triangle limit must be a whole number of at least 100.", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                TextThreshold.Focus();
                return;
            }
            if (!float.TryParse(TextStep.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float step) || step < 50f || step > 450f)
            {
                ShowTabOf(TextStep);
                Win.MessageBox.Show(this, "The max step height must be between 50 and 450 mm.", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                TextStep.Focus();
                return;
            }

            if (!ReadPhases(out string existingPhase, out string newPhase)) { return; }

            bool viewOnly = CheckViewOnly.IsChecked == true;
            List<string> enabled = SelectedKeys();
            if (enabled.Count == 0 && !viewOnly)
            {
                ShowTabOf(GridGroups);
                Win.MessageBox.Show(this, "Tick at least one category to load (or load only what the active view shows).", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                return;
            }

            if (enabled.Count > 0) { _settings.EnabledCategories = enabled; }
            _settings.ActiveViewOnly = viewOnly;
            _settings.SkipHelperGeometry = CheckSkipHelpers.IsChecked == true;
            _settings.HelperSubcategoryKeywords = (TextHelperKeywords.Text ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToList();
            _settings.TriangleThreshold = threshold;
            _settings.OverLimit = RadioSkip.IsChecked == true ? OverLimitMode.Skip : OverLimitMode.Proxy;
            _settings.Colour = RadioRealistic.IsChecked == true ? ColourMode.Realistic
                : RadioMaterial.IsChecked == true ? ColourMode.Material
                : ColourMode.Whitecard;
            _settings.ExtractTextures = CheckTextures.IsChecked == true;
            _settings.ProxyMissingTextures = CheckProxyTextures.IsChecked == true;
            _settings.TextureMaxSize = MaterialData.TEXTURE_SIZES[Math.Clamp(ComboTextureSize.SelectedIndex, 0, MaterialData.TEXTURE_SIZES.Length - 1)];
            _settings.MaxStepHeightMm = step;
            _settings.Msaa = ComboMsaa.SelectedIndex switch { 2 => 4, 1 => 2, _ => 0 };
            _settings.FieldOfView = (float)SliderFov.Value;
            _settings.MouseSensitivity = (float)SliderSensitivity.Value;
            _settings.InvertY = CheckInvertY.IsChecked == true;
            _settings.VSync = CheckVSync.IsChecked == true;
            _settings.LoadComments = CheckComments.IsChecked == true;
            _settings.GizmoSnap = CheckSnap.IsChecked == true;
            _settings.SnapMoveMm = LaunchSettings.SNAP_MOVE_STEPS_MM[Math.Max(ComboSnapMove.SelectedIndex, 0)];
            _settings.SnapAngleDeg = LaunchSettings.SNAP_ANGLE_STEPS_DEG[Math.Max(ComboSnapAngle.SelectedIndex, 0)];
            _settings.ShadowQuality = (BimGo.Scene.ShadowQuality)Math.Clamp(ComboShadowQuality.SelectedIndex, 0, 2);
            _settings.ArtificialLights = (BimGo.Scene.ArtificialLightMode)Math.Clamp(ComboArtificialLights.SelectedIndex, 0, 2);
            _settings.ExistingPhase = existingPhase;
            _settings.NewPhase = newPhase;
            _settings.ExtraParameters = _knownParameters.Where(_pickedParameters.Contains).ToList();
            _settings.SetLinksFor(_links.HostKey, SelectedLinks());

            // What a picked profile set for the walkthrough only (unchanged unless a profile was picked here)
            _settings.AmbientOcclusion = _display.AmbientOcclusion;
            _settings.BloomIntensity = _display.BloomIntensity;
            _settings.Reflections = _display.Reflections;
            _settings.ReflectionThreshold = _display.ReflectionThreshold;
            _settings.ReflectionProbes = _display.ReflectionProbes;
            _settings.ProbeResolution = _display.ProbeResolution;

            _settings.SidecarsBesideModel = CheckSidecarsBesideModel.IsChecked == true;
            _settings.LastOptionsTab = Math.Max(Tabs.SelectedIndex, 0);
            _settings.Sanitise();

            DialogResult = true;
        }

        /// <summary>
        /// Lists the model's parameter names (sampled per category, instance and type parameters).
        /// </summary>
        private void ButtonScanParameters_Click(object sender, Win.RoutedEventArgs e)
        {
            if (_scanParameterNames == null) { return; }
            try
            {
                Cursor = Win.Input.Cursors.Wait;
                List<string> names = _scanParameterNames();
                foreach (string name in names) { _knownParameters.Add(name); }
                _scanned = true;
                RebuildParameterList();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Parameter scan failed: {ex}");
                Win.MessageBox.Show(this, $"The parameter scan failed:\n{ex.Message}", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
            }
            finally
            {
                Cursor = null;
            }
        }

        /// <summary>
        /// Filter text changed.
        /// </summary>
        private void TextParameterFilter_TextChanged(object sender, Wpf.TextChangedEventArgs e)
        {
            RebuildParameterList();
        }

        /// <summary>
        /// A parameter tick box changed.
        /// </summary>
        private void ParameterCheck_Click(object sender, Win.RoutedEventArgs e)
        {
            if (sender is not Wpf.CheckBox check || check.Tag is not string name) { return; }

            if (check.IsChecked == true)
            {
                if (_pickedParameters.Count >= LaunchSettings.MAX_EXTRA_PARAMETERS)
                {
                    check.IsChecked = false;
                    Win.MessageBox.Show(this, $"Pick up to {LaunchSettings.MAX_EXTRA_PARAMETERS} parameters.", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Information);
                    return;
                }
                _pickedParameters.Add(name);
            }
            else
            {
                _pickedParameters.Remove(name);
            }
            UpdateParameterSummary();
        }

        /// <summary>
        /// Cancel.
        /// </summary>
        private void ButtonCancel_Click(object sender, Win.RoutedEventArgs e)
        {
            DialogResult = false;
        }

        #endregion

        #region Quality profile and tabs

        /// <summary>
        /// Writes the visible profile-governed controls (colour, anti-aliasing, shadow quality, lights) into a settings object.
        /// </summary>
        private void ReadDisplayControls(LaunchSettings target)
        {
            target.Colour = RadioRealistic.IsChecked == true ? ColourMode.Realistic
                : RadioMaterial.IsChecked == true ? ColourMode.Material
                : ColourMode.Whitecard;
            target.Msaa = ComboMsaa.SelectedIndex switch { 2 => 4, 1 => 2, _ => 0 };
            target.ShadowQuality = (BimGo.Scene.ShadowQuality)Math.Clamp(ComboShadowQuality.SelectedIndex, 0, 2);
            target.ArtificialLights = (BimGo.Scene.ArtificialLightMode)Math.Clamp(ComboArtificialLights.SelectedIndex, 0, 2);
        }

        /// <summary>
        /// Shows the profile the current choices match (Custom when none) and what it means.
        /// </summary>
        private void RefreshProfile()
        {
            if (_display == null || ComboProfile == null || TextProfile == null || ComboArtificialLights == null || RadioRealistic == null) { return; }
            ReadDisplayControls(_display);
            QualityProfile profile = QualityProfiles.Detect(_display);

            bool wasUpdating = _updating;
            _updating = true;
            ComboProfile.SelectedIndex = (int)profile;
            _updating = wasUpdating;

            TextProfile.Text = profile switch
            {
                QualityProfile.Basic => "Basic: whitecard, 2x anti-aliasing, ambient occlusion and low shadow quality; artificial lights, bloom and reflections off.",
                QualityProfile.Medium => "Medium: material colours, 2x anti-aliasing, ambient occlusion, medium shadows, glow + light, bloom, sky reflections on the shiniest surfaces.",
                QualityProfile.Realistic => "Realistic: textures (extraction ticked), 4x anti-aliasing, ambient occlusion, high shadows, glow + light, bloom, probe reflections on all reflective surfaces.",
                _ => "Custom: your own mix. A profile sets the colour mode, anti-aliasing, shadow quality and lights here, and ambient occlusion, bloom and reflections in the walkthrough. Shadows on / off stays with each model (O)."
            };
        }

        /// <summary>
        /// A profile was picked: sets the controls it governs (and, for Realistic, ticks the extraction).
        /// </summary>
        private void ComboProfile_SelectionChanged(object sender, Wpf.SelectionChangedEventArgs e)
        {
            if (_updating || _display == null) { return; }
            var profile = (QualityProfile)Math.Clamp(ComboProfile.SelectedIndex, 0, 3);
            if (profile == QualityProfile.Custom)
            {
                RefreshProfile(); // Custom is shown, not picked
                return;
            }

            ReadDisplayControls(_display);
            QualityProfiles.Apply(_display, profile);

            _updating = true;
            RadioWhitecard.IsChecked = _display.Colour == ColourMode.Whitecard;
            RadioMaterial.IsChecked = _display.Colour == ColourMode.Material;
            RadioRealistic.IsChecked = _display.Colour == ColourMode.Realistic;
            ComboMsaa.SelectedIndex = _display.Msaa >= 4 ? 2 : _display.Msaa >= 2 ? 1 : 0;
            ComboShadowQuality.SelectedIndex = Math.Clamp((int)_display.ShadowQuality, 0, 2);
            ComboArtificialLights.SelectedIndex = Math.Clamp((int)_display.ArtificialLights, 0, 2);
            if (profile == QualityProfile.Realistic) { CheckTextures.IsChecked = true; }
            _updating = false;

            UpdateTextureControls();
            RefreshProfile();
        }

        /// <summary>
        /// A colour radio changed (Whitecard / Material; Realistic has its own handler).
        /// </summary>
        private void DisplayControl_Changed(object sender, Win.RoutedEventArgs e)
        {
            if (!_updating) { RefreshProfile(); }
        }

        /// <summary>
        /// Anti-aliasing, shadow quality or lights changed (fires during InitializeComponent too, hence the guard).
        /// </summary>
        private void DisplayCombo_Changed(object sender, Wpf.SelectionChangedEventArgs e)
        {
            if (!_updating) { RefreshProfile(); }
        }

        /// <summary>
        /// Selects the tab holding a control (before a validation message focuses it).
        /// </summary>
        private void ShowTabOf(Win.DependencyObject control)
        {
            Win.DependencyObject node = control;
            while (node != null && node is not Wpf.TabItem)
            {
                node = Win.LogicalTreeHelper.GetParent(node);
            }
            if (node is Wpf.TabItem tab) { tab.IsSelected = true; }
        }

        #endregion

        #region Phases and snap

        /// <summary>
        /// Fills the snap controls from the settings.
        /// </summary>
        private void LoadSnap()
        {
            ComboSnapMove.Items.Clear();
            foreach (float mm in LaunchSettings.SNAP_MOVE_STEPS_MM) { ComboSnapMove.Items.Add($"{mm:0} mm"); }
            ComboSnapAngle.Items.Clear();
            foreach (float degrees in LaunchSettings.SNAP_ANGLE_STEPS_DEG) { ComboSnapAngle.Items.Add($"{degrees:0}°"); }

            CheckSnap.IsChecked = _settings.GizmoSnap;
            ComboSnapMove.SelectedIndex = Math.Max(0, Array.IndexOf(LaunchSettings.SNAP_MOVE_STEPS_MM,
                LaunchSettings.NearestStep(LaunchSettings.SNAP_MOVE_STEPS_MM, _settings.SnapMoveMm)));
            ComboSnapAngle.SelectedIndex = Math.Max(0, Array.IndexOf(LaunchSettings.SNAP_ANGLE_STEPS_DEG,
                LaunchSettings.NearestStep(LaunchSettings.SNAP_ANGLE_STEPS_DEG, _settings.SnapAngleDeg)));
        }

        /// <summary>
        /// Fills the phase pickers: the saved names when this model has them, else the defaults.
        /// </summary>
        private void LoadPhases()
        {
            ComboExistingPhase.Items.Clear();
            ComboNewPhase.Items.Clear();

            if (_phases.Names.Count == 0)
            {
                ComboExistingPhase.IsEnabled = false;
                ComboNewPhase.IsEnabled = false;
                TextPhaseHint.Text = "This model has no phases: the Demolish gun can only delete.";
                return;
            }

            ComboExistingPhase.Items.Add(NO_PHASE);
            foreach (string name in _phases.Names)
            {
                ComboExistingPhase.Items.Add(name);
                ComboNewPhase.Items.Add(name);
            }

            // New: the saved name, else the default. Existing: the saved name, else the phase before the new one.
            string newName = Pick(_settings.NewPhase, _phases.DefaultNew) ?? _phases.Names[^1];
            int newIndex = _phases.Names.IndexOf(newName);
            string existingName = Pick(_settings.ExistingPhase, newIndex > 0 ? _phases.Names[newIndex - 1] : null);
            ComboNewPhase.SelectedItem = newName;
            ComboExistingPhase.SelectedItem = existingName ?? NO_PHASE;
        }

        /// <summary>
        /// The saved name if this model has it (matched case-insensitively), else the default.
        /// </summary>
        private string Pick(string saved, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(saved))
            {
                string match = _phases.Names.FirstOrDefault(n => string.Equals(n, saved.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match != null) { return match; }
            }
            return fallback;
        }

        /// <summary>
        /// Validates the phase choice: an existing phase is needed (unless the new phase is the first) and must come
        /// before the new phase.
        /// </summary>
        /// <returns>False (after telling the user) if the existing phase isn't before the new one.</returns>
        private bool ReadPhases(out string existingPhase, out string newPhase)
        {
            existingPhase = _settings.ExistingPhase ?? string.Empty;
            newPhase = _settings.NewPhase ?? string.Empty;
            if (_phases.Names.Count == 0) { return true; }

            string chosenNew = ComboNewPhase.SelectedItem as string;
            string chosenExisting = ComboExistingPhase.SelectedItem as string;
            if (chosenExisting == NO_PHASE) { chosenExisting = null; }

            int newIndex = _phases.Names.IndexOf(chosenNew);
            int existingIndex = chosenExisting == null ? -1 : _phases.Names.IndexOf(chosenExisting);
            if (chosenExisting == null && newIndex > 0)
            {
                ShowTabOf(ComboExistingPhase);
                Win.MessageBox.Show(this, "Pick the existing phase (usually the phase before the new one).", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                ComboExistingPhase.Focus();
                return false;
            }
            if (chosenExisting != null && existingIndex >= newIndex)
            {
                ShowTabOf(ComboExistingPhase);
                Win.MessageBox.Show(this, "The existing phase must come before the new phase.", "BimGo", Win.MessageBoxButton.OK, Win.MessageBoxImage.Warning);
                ComboExistingPhase.Focus();
                return false;
            }

            // Names are stored (the dialog matches them again for the next model, falling back to its defaults)
            newPhase = chosenNew ?? string.Empty;
            existingPhase = chosenExisting ?? string.Empty;
            return true;
        }

        #endregion

        #region Active view and helper geometry

        /// <summary>
        /// The view-only box changed: the category cards don't apply while it is ticked.
        /// </summary>
        private void CheckViewOnly_Click(object sender, Win.RoutedEventArgs e)
        {
            UpdateViewOnly();
        }

        /// <summary>
        /// The helper geometry box changed: the keywords only apply while it is ticked.
        /// </summary>
        private void CheckSkipHelpers_Click(object sender, Win.RoutedEventArgs e)
        {
            TextHelperKeywords.IsEnabled = CheckSkipHelpers.IsChecked == true;
        }

        /// <summary>
        /// Enables / disables the category cards and describes the active view.
        /// </summary>
        private void UpdateViewOnly()
        {
            bool viewOnly = CheckViewOnly.IsChecked == true;
            GridGroups.IsEnabled = !viewOnly;
            CheckHeavy.IsEnabled = !viewOnly;
            TextHelperKeywords.IsEnabled = CheckSkipHelpers.IsChecked == true;

            if (!_view.Available)
            {
                TextViewOnly.Text = "The active view doesn't show model elements. Open a 3D view, plan or section to use this.";
            }
            else
            {
                string hint = _view.Is3D ? string.Empty : " A 3D view works best: plans and sections only include what their view range or far clip reaches.";
                TextViewOnly.Text = $"“{_view.Name}” shows about {_view.ElementCount:N0} elements. When ticked, everything visible there comes in " +
                    "(its visibility/graphics, filters, section box, hidden elements and design options apply; the category ticks, " +
                    "phase and design option rules below don't). Ticked links add what the view shows of them." + hint;
            }
            UpdateEstimate();
        }

        /// <summary>
        /// True for catalog definitions the category cards offer (not the heavy set, which has its own box, and not
        /// the active-view-only "other" bucket).
        /// </summary>
        private static bool IsOffered(CategoryDef def) => !def.Heavy && def.BuiltInCategoryNames.Length > 0;

        #endregion

        #region Linked models

        /// <summary>
        /// One row per link instance, grouped under its file (a file with several instances gets a tick box that sets
        /// them all). Unloaded links are listed but can't be ticked. Ticks come from the saved choice for this model.
        /// </summary>
        private void BuildLinkList()
        {
            PanelLinks.Children.Clear();
            _linkChecks.Clear();
            if (_links.Items.Count == 0)
            {
                PanelLinks.Children.Add(new Wpf.TextBlock { Text = "This model has no Revit links.", Foreground = (Media.Brush)FindResource("Muted") });
                return;
            }

            var saved = new HashSet<string>(_settings.LinksFor(_links.HostKey), StringComparer.Ordinal);
            foreach (IGrouping<string, LinkChoice> file in _links.Items.GroupBy(l => l.FileName))
            {
                List<LinkChoice> instances = file.ToList();
                Wpf.CheckBox fileCheck = null;
                if (instances.Count > 1)
                {
                    fileCheck = new Wpf.CheckBox
                    {
                        Content = $"{file.Key} ({instances.Count} instances)",
                        FontWeight = Win.FontWeights.SemiBold,
                        Margin = new Win.Thickness(0, 4, 0, 2),
                        IsEnabled = instances.Any(i => i.IsLoaded)
                    };
                    PanelLinks.Children.Add(fileCheck);
                }

                var checks = new List<Wpf.CheckBox>();
                foreach (LinkChoice link in instances)
                {
                    var check = new Wpf.CheckBox
                    {
                        Content = link.IsLoaded ? link.Name : $"{link.Name} (not loaded)",
                        Tag = link.UniqueId,
                        IsEnabled = link.IsLoaded,
                        IsChecked = link.IsLoaded && saved.Contains(link.UniqueId),
                        Margin = new Win.Thickness(fileCheck == null ? 0 : 22, 2, 0, 2),
                        FontWeight = fileCheck == null ? Win.FontWeights.SemiBold : Win.FontWeights.Normal,
                        ToolTip = link.IsLoaded ? null : "Load this link in Revit (Manage Links) to include it."
                    };
                    check.Click += (_, _) =>
                    {
                        if (fileCheck != null) { RefreshFileCheck(fileCheck, checks); }
                        UpdateEstimate();
                    };
                    checks.Add(check);
                    _linkChecks[link.UniqueId] = check;
                    PanelLinks.Children.Add(check);
                }

                if (fileCheck != null)
                {
                    fileCheck.Click += (_, _) =>
                    {
                        // Clicking an indeterminate box resolves to "all on"
                        bool on = fileCheck.IsChecked != false;
                        foreach (Wpf.CheckBox check in checks.Where(c => c.IsEnabled)) { check.IsChecked = on; }
                        RefreshFileCheck(fileCheck, checks);
                        UpdateEstimate();
                    };
                    RefreshFileCheck(fileCheck, checks);
                }
            }
        }

        /// <summary>
        /// Sets a file's tick box to on / off / indeterminate from its instances.
        /// </summary>
        private static void RefreshFileCheck(Wpf.CheckBox fileCheck, List<Wpf.CheckBox> checks)
        {
            int enabled = checks.Count(c => c.IsEnabled);
            int on = checks.Count(c => c.IsEnabled && c.IsChecked == true);
            fileCheck.IsChecked = on == 0 ? false : on == enabled ? true : (bool?)null;
        }

        /// <summary>
        /// The ticked link instances (UniqueIds).
        /// </summary>
        private List<string> SelectedLinks() =>
            _linkChecks.Where(p => p.Value.IsEnabled && p.Value.IsChecked == true).Select(p => p.Key).ToList();

        #endregion

        #region Helpers

        /// <summary>
        /// The ticked category keys (including the heavy set if ticked).
        /// </summary>
        private List<string> SelectedKeys()
        {
            var keys = _categoryChecks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
            if (CheckHeavy.IsChecked == true)
            {
                keys.AddRange(CategoryCatalog.All.Where(d => d.Heavy).Select(d => d.Key));
            }
            return keys;
        }

        /// <summary>
        /// Sets each group box to on / off / indeterminate from its categories.
        /// </summary>
        private void RefreshGroupChecks()
        {
            _updating = true;
            for (int g = 0; g < 3; g++)
            {
                var defs = CategoryCatalog.All.Where(d => (int)d.Group == g && IsOffered(d)).ToList();
                int on = defs.Count(d => _categoryChecks[d.Key].IsChecked == true);
                _groupChecks[g].IsChecked = on == 0 ? false : on == defs.Count ? true : (bool?)null;
            }
            _updating = false;
        }

        /// <summary>
        /// Updates the footer estimate.
        /// </summary>
        private void UpdateEstimate()
        {
            if (TextEstimate == null || CheckViewOnly == null) { return; }
            int total = 0;
            foreach (string key in SelectedKeys())
            {
                if (CategoryCatalog.Find(key) is CategoryDef def) { total += _counts[def.Index]; }
            }
            if (CheckViewOnly.IsChecked == true) { total = _view.ElementCount; }
            int links = SelectedLinks().Count;
            TextEstimate.Text = links == 0
                ? $"About {total:N0} elements"
                : $"About {total:N0} elements + {links} linked model{(links == 1 ? string.Empty : "s")}";
        }

        /// <summary>
        /// Shows the known parameter names matching the filter (picked ones first) as tick boxes.
        /// </summary>
        private void RebuildParameterList()
        {
            if (ListParameters == null) { return; }

            string filter = TextParameterFilter?.Text?.Trim() ?? string.Empty;
            IEnumerable<string> matches = _knownParameters
                .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(n => _pickedParameters.Contains(n) ? 0 : 1);

            ListParameters.Items.Clear();
            foreach (string name in matches.Take(500))
            {
                var check = new Wpf.CheckBox
                {
                    Content = name,
                    Tag = name,
                    IsChecked = _pickedParameters.Contains(name),
                    Margin = new Win.Thickness(2, 1, 2, 1),
                    FontSize = 12
                };
                check.Click += ParameterCheck_Click;
                ListParameters.Items.Add(check);
            }
            UpdateParameterSummary();
        }

        /// <summary>
        /// Updates the parameter summary line.
        /// </summary>
        private void UpdateParameterSummary()
        {
            if (TextParameterSummary == null) { return; }
            string picked = _pickedParameters.Count == 0
                ? "None selected."
                : $"{_pickedParameters.Count} selected: {string.Join(", ", _knownParameters.Where(_pickedParameters.Contains))}.";
            string hint = _scanned
                ? $" {_knownParameters.Count:N0} names found."
                : " Press Scan model to list the parameters in this model.";
            TextParameterSummary.Text = picked + hint + " Values are stored once per distinct value, so files stay small.";
        }

        /// <summary>
        /// Updates the FOV and sensitivity readouts.
        /// </summary>
        private void UpdateSliderLabels()
        {
            if (TextFov == null || TextSensitivity == null) { return; }
            TextFov.Text = $"{SliderFov.Value:0}°";
            TextSensitivity.Text = SliderSensitivity.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        #endregion
    }

    /// <summary>
    /// The active view, as the Options dialog describes it.
    /// </summary>
    internal sealed class ViewChoice
    {
        /// <summary>False when the active view can't show model elements (schedules, sheets, legends…).</summary>
        public bool Available { get; init; }

        /// <summary>The view's name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>True for a 3D view.</summary>
        public bool Is3D { get; init; }

        /// <summary>Roughly how many model elements the view shows.</summary>
        public int ElementCount { get; init; }
    }

    /// <summary>
    /// One link instance the Options dialog offers.
    /// </summary>
    internal sealed class LinkChoice
    {
        /// <summary>The RevitLinkInstance UniqueId (saved).</summary>
        public string UniqueId { get; init; } = string.Empty;

        /// <summary>The instance name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>The linked file (rows are grouped by it).</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>False when the link isn't loaded (shown, can't be ticked).</summary>
        public bool IsLoaded { get; init; }
    }

    /// <summary>
    /// The link instances the Options dialog offers and the host model key the choice is saved under.
    /// </summary>
    internal sealed class LinkChoices
    {
        /// <summary>The host model key (<see cref="LaunchSettings.LinkedModels"/>).</summary>
        public string HostKey { get; init; } = string.Empty;

        /// <summary>The host model's BimGo folder (its texture choices are summarised from there).</summary>
        public string ModelFolder { get; init; } = string.Empty;

        /// <summary>The link instances, sorted by file then name.</summary>
        public List<LinkChoice> Items { get; init; } = new();
    }

    /// <summary>
    /// The phase names the Options dialog offers, with the defaults used when nothing is saved.
    /// </summary>
    internal sealed class PhaseChoices
    {
        /// <summary>The model's phases in sequence order.</summary>
        public List<string> Names { get; init; } = new();

        /// <summary>The default new phase (the launch view's phase, else the last phase), or null.</summary>
        public string DefaultNew { get; init; }
    }
}
