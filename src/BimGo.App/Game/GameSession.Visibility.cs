using BimGo.Audio;
using BimGo.Format;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Walkthrough-only visibility: elements hidden with the Scan gun (I), category isolation (Shift+I), and the
    /// category / link toggles of the pause menu. None of it edits the model or enters the journal; it is saved with
    /// the model (visibility.json in a .bimgo, or a sidecar beside the Revit model in live sessions) and restored on
    /// open. SHOW ALL in the pause menu brings everything back.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private readonly bool[] _userHidden;
        private int _userHiddenCount;

        // Category visibility before Shift+I isolated one category (null when nothing is isolated)
        private bool[] _isolateBackup;

        private int _visibilityRevision, _savedVisibilityRevision, _sidecarVisibilityRevision;
        private float _visibilitySidecarTimer;
        private string _visibilitySidecarPath;

        #endregion

        #region State

        /// <summary>True if the visibility changed since the file was saved (file mode).</summary>
        private bool VisibilityDirty => _visibilityRevision != _savedVisibilityRevision;

        /// <summary>True if an element is hidden in the walkthrough only (Scan gun, I).</summary>
        public bool IsUserHidden(int element) => element >= 0 && _userHidden[element];

        /// <summary>
        /// How many things are hidden: elements, loaded categories that are off, and links that are off (the pause
        /// menu offers SHOW ALL when this is above zero).
        /// </summary>
        private int HiddenThingsCount()
        {
            int count = _userHiddenCount;
            for (int c = 0; c < _categoryVisible.Length; c++)
            {
                if (Scene.CategoryLoaded[c] && !_categoryVisible[c]) { count++; }
            }
            for (int l = 1; l < _linkVisible.Length; l++)
            {
                if (!_linkVisible[l]) { count++; }
            }
            return count;
        }

        #endregion

        #region Load and save

        /// <summary>
        /// Restores the saved visibility (file, else live sidecar). Call once the renderer and masks exist.
        /// </summary>
        private void InitialiseVisibility()
        {
            VisibilitySettings saved;
            if (IsFileMode)
            {
                saved = _options.Document?.Visibility;
            }
            else
            {
                _visibilitySidecarPath = VisibilityFiles.SidecarFor(Scene.CommentsPath);
                saved = VisibilityFiles.Read(_visibilitySidecarPath, out string error);
                if (error != null) { Utilities.Log_Utils.Write(error); }
            }
            if (saved == null || saved.IsEmpty) { return; }

            foreach (string key in saved.HiddenCategories)
            {
                if (CategoryCatalog.Find(key) is CategoryDef def && Scene.CategoryLoaded[def.Index]) { _categoryVisible[def.Index] = false; }
            }

            var linkByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LinkInfo link in Scene.Links)
            {
                if (!string.IsNullOrEmpty(link.InstanceUniqueId)) { linkByKey.TryAdd(link.InstanceUniqueId, link.Index); }
            }
            foreach (string key in saved.HiddenLinks)
            {
                if (linkByKey.TryGetValue(key, out int index)) { _linkVisible[index] = false; }
            }

            // Elements: host by UniqueId (else id), linked by (link, UniqueId)
            Dictionary<(int, string), int> linked = null;
            int missing = 0;
            foreach (HiddenElement item in saved.HiddenElements)
            {
                int element = -1;
                if (string.IsNullOrEmpty(item.Link))
                {
                    if (string.IsNullOrEmpty(item.UniqueId) || !_elementIndexByUniqueId.TryGetValue(item.UniqueId, out element))
                    {
                        if (!_elementIndexById.TryGetValue(item.Id, out element)) { element = -1; }
                    }
                }
                else if (linkByKey.TryGetValue(item.Link, out int link) && !string.IsNullOrEmpty(item.UniqueId))
                {
                    linked ??= BuildLinkedIndex();
                    if (!linked.TryGetValue((link, item.UniqueId), out element)) { element = -1; }
                }

                if (element >= 0) { SetUserHidden(element, true); }
                else { missing++; }
            }
            if (missing > 0) { Utilities.Log_Utils.Write($"Visibility: {missing} hidden element(s) are not in this model any more."); }

            RefreshMasks();
            _sidecarVisibilityRevision = _visibilityRevision;
        }

        /// <summary>
        /// (link number, UniqueId) → element index for linked elements (built on demand: only restoring needs it).
        /// </summary>
        private Dictionary<(int, string), int> BuildLinkedIndex()
        {
            var index = new Dictionary<(int, string), int>();
            ElementRecord[] elements = Scene.Elements;
            for (int e = 0; e < elements.Length; e++)
            {
                if (elements[e].IsLinked && !string.IsNullOrEmpty(elements[e].UniqueId)) { index.TryAdd((elements[e].Link, elements[e].UniqueId), e); }
            }
            return index;
        }

        /// <summary>
        /// The current visibility as saved with the model.
        /// </summary>
        private VisibilitySettings ToVisibilitySettings()
        {
            var settings = new VisibilitySettings();
            foreach (CategoryDef def in CategoryCatalog.All)
            {
                if (Scene.CategoryLoaded[def.Index] && !_categoryVisible[def.Index]) { settings.HiddenCategories.Add(def.Key); }
            }
            foreach (LinkInfo link in Scene.Links)
            {
                if (!_linkVisible[link.Index] && !string.IsNullOrEmpty(link.InstanceUniqueId)) { settings.HiddenLinks.Add(link.InstanceUniqueId); }
            }
            if (_userHiddenCount > 0)
            {
                ElementRecord[] elements = Scene.Elements;
                for (int e = 0; e < elements.Length; e++)
                {
                    if (!_userHidden[e]) { continue; }
                    settings.HiddenElements.Add(new HiddenElement
                    {
                        Link = Scene.LinkOf(elements[e])?.InstanceUniqueId,
                        UniqueId = string.IsNullOrEmpty(elements[e].UniqueId) ? null : elements[e].UniqueId,
                        Id = elements[e].ElementId
                    });
                }
            }
            return settings;
        }

        /// <summary>
        /// Records a visibility change (dirty file / delayed live sidecar).
        /// </summary>
        private void VisibilityChanged()
        {
            _visibilityRevision++;
            _visibilitySidecarTimer = SIDECAR_DELAY;
            UpdateTitle();
        }

        /// <summary>
        /// Per frame: writes the live sidecar once changes settle.
        /// </summary>
        private void UpdateVisibilitySidecar(float dt)
        {
            if (_visibilitySidecarPath == null || _sidecarVisibilityRevision == _visibilityRevision) { return; }
            _visibilitySidecarTimer -= dt;
            if (_visibilitySidecarTimer <= 0f) { FlushVisibilitySidecar(); }
        }

        /// <summary>
        /// Writes the live sidecar if it is behind (also on shutdown). An empty state still overwrites an old file.
        /// </summary>
        private void FlushVisibilitySidecar()
        {
            if (_visibilitySidecarPath == null || _sidecarVisibilityRevision == _visibilityRevision) { return; }
            _sidecarVisibilityRevision = _visibilityRevision;
            if (!VisibilityFiles.Write(_visibilitySidecarPath, ToVisibilitySettings(), out string error)) { Toast(error, 4f, important: true); }
        }

        #endregion

        #region Actions

        /// <summary>
        /// Hides or shows one static element in the walkthrough only (drawing, picking, collision, shadows).
        /// </summary>
        private void SetUserHidden(int element, bool hidden)
        {
            if (element < 0 || _userHidden[element] == hidden) { return; }
            _userHidden[element] = hidden;
            _userHiddenCount += hidden ? 1 : -1;
            _sceneRevision++;
            _renderer.SetElementHidden(element, hidden || _hidden[element]);

            bool visible = !hidden && !_hidden[element] && _groupVisible[Rendering.SceneBatches.GroupOf(Scene.Elements[element])];
            _pickMask[element] = visible;
            _collisionMask[element] = visible && Scene.Elements[element].CategoryIndex != _doorCategory;
        }

        /// <summary>
        /// Scan gun, I: hides the target in the walkthrough only.
        /// </summary>
        /// <param name="element">The element.</param>
        /// <param name="dynamicId">The moved / cloned instance under the crosshair, or 0.</param>
        public void HideElement(int element, int dynamicId)
        {
            if (element < 0) { return; }
            if (dynamicId > 0)
            {
                Sound.Play(SoundId.Error);
                Toast("Moved or cloned elements can't be hidden: demolish the clone, or undo the move", important: true);
                return;
            }

            SetUserHidden(element, true);
            VisibilityChanged();
            Sound.Play(SoundId.UiClick);
            Toast($"Hidden: {Scene.Elements[element].Name} ({_userHiddenCount} hidden · Esc → SHOW ALL brings them back)");
        }

        /// <summary>
        /// Scan gun, Shift+I: shows only the target's category; pressed again, restores the categories.
        /// </summary>
        public void ToggleIsolateCategory(int element)
        {
            Sound.Play(SoundId.UiClick);
            if (_isolateBackup != null)
            {
                Array.Copy(_isolateBackup, _categoryVisible, _categoryVisible.Length);
                _isolateBackup = null;
                RefreshMasks();
                VisibilityChanged();
                Toast("Isolation ended: categories shown as before");
                return;
            }
            if (element < 0) { return; }

            int category = Scene.Elements[element].CategoryIndex;
            _isolateBackup = (bool[])_categoryVisible.Clone();
            for (int c = 0; c < _categoryVisible.Length; c++) { _categoryVisible[c] = c == category && Scene.CategoryLoaded[c]; }
            RefreshMasks();
            VisibilityChanged();
            Toast($"Showing only {CategoryCatalog.All[category].Label} (Shift+I again shows the rest)");
        }

        /// <summary>
        /// Pause menu SHOW ALL: every hidden element, category and link comes back.
        /// </summary>
        private void ShowAll()
        {
            for (int e = 0; e < _userHidden.Length && _userHiddenCount > 0; e++)
            {
                if (_userHidden[e]) { SetUserHidden(e, false); }
            }
            for (int c = 0; c < _categoryVisible.Length; c++) { _categoryVisible[c] = Scene.CategoryLoaded[c]; }
            Array.Fill(_linkVisible, true);
            _isolateBackup = null;
            RefreshMasks();
            VisibilityChanged();
            Sound.Play(SoundId.UiClick);
            Toast("All hidden elements, categories and links are shown again");
        }

        #endregion
    }
}
