using System.Text.Json;
using BimGo.Format;
using BimGo.Scene;

// The class belongs to the Clashes namespace
namespace BimGo.Clashes
{
    /// <summary>What an imported tests / sets file holds.</summary>
    public enum ClashSetupKind
    {
        /// <summary>Clash tests (with the search sets they use).</summary>
        Tests,

        /// <summary>Search sets only.</summary>
        Sets
    }

    /// <summary>What to do with an imported test / set whose name is already taken (one choice for the whole file).</summary>
    public enum ClashNameClash
    {
        /// <summary>The imported one replaces the existing one's definition (its id, and so its clashes, stay).</summary>
        Replace,

        /// <summary>Both are kept: the imported one gets " (2)".</summary>
        KeepBoth,

        /// <summary>The imported one is left out.</summary>
        Skip
    }

    /// <summary>A link named in an exported file (rules hold the instance UniqueId; the name helps match it elsewhere).</summary>
    public sealed class ClashSetupLink
    {
        /// <summary>The link instance's UniqueId (the rule value).</summary>
        public string UniqueId { get; set; } = string.Empty;

        /// <summary>The link's name where it was exported.</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// QOL round: a tests or sets file (EXPORT TESTS… / EXPORT SETS… in the Clashes panel), for reusing clash setups
    /// across models. JSON with a header; never clashes or history.
    /// </summary>
    public sealed class ClashSetupFile
    {
        /// <summary><see cref="ClashSetupFiles.TESTS_FORMAT"/> or <see cref="ClashSetupFiles.SETS_FORMAT"/>.</summary>
        public string Format { get; set; }

        /// <summary>The shape's version (1).</summary>
        public int Version { get; set; } = ClashSetupFiles.VERSION;

        /// <summary>The model it came from (for the import summary), or null.</summary>
        public string ModelTitle { get; set; }

        /// <summary>Who wrote it ("BimGo X.X.X"), or null.</summary>
        public string Generator { get; set; }

        /// <summary>When (UTC), or null.</summary>
        public DateTime? ExportedUtc { get; set; }

        /// <summary>The sets (a tests file: the sets its tests use).</summary>
        public List<SearchSet> Sets { get; set; } = new();

        /// <summary>The tests (empty in a sets file).</summary>
        public List<ClashTest> Tests { get; set; } = new();

        /// <summary>The links the rules name, with their names.</summary>
        public List<ClashSetupLink> Links { get; set; } = new();

        /// <summary>Which kind of file this is.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public ClashSetupKind Kind => string.Equals(Format, ClashSetupFiles.TESTS_FORMAT, StringComparison.OrdinalIgnoreCase) ? ClashSetupKind.Tests : ClashSetupKind.Sets;
    }

    /// <summary>A rule value the importing model doesn't have (a link or a level), and what it will become.</summary>
    public sealed class ClashSetupMissing
    {
        /// <summary><see cref="RuleField.LINK"/> or <see cref="RuleField.LEVEL"/>.</summary>
        public string Field { get; set; }

        /// <summary>The value in the file (a link's UniqueId, a level's name).</summary>
        public string Value { get; set; }

        /// <summary>What people read (a link's name from the file, a level's name).</summary>
        public string Label { get; set; }

        /// <summary>How many rules use it.</summary>
        public int Uses { get; set; }

        /// <summary>
        /// The replacement: a current link's UniqueId / a level's name; null = leave the value as it is (it matches
        /// nothing here); <see cref="ClashSetupFiles.DROP"/> = take the value out of its rules.
        /// </summary>
        public string Replacement { get; set; }

        /// <summary>True when the replacement was found by name (shown as "matched by name").</summary>
        public bool AutoMatched { get; set; }
    }

    /// <summary>What an import will meet: the file, names already taken and values to reconcile.</summary>
    public sealed class ClashSetupPlan
    {
        /// <summary>The file read.</summary>
        public ClashSetupFile File { get; set; }

        /// <summary>Imported tests (tests file) or sets (sets file) whose name is already taken.</summary>
        public List<string> NameClashes { get; set; } = new();

        /// <summary>Links and levels the file's rules name that this model doesn't have.</summary>
        public List<ClashSetupMissing> Missing { get; set; } = new();

        /// <summary>The choice for <see cref="NameClashes"/>.</summary>
        public ClashNameClash OnNameClash { get; set; } = ClashNameClash.KeepBoth;
    }

    /// <summary>What an import changed.</summary>
    public readonly record struct ClashSetupResult(int TestsAdded, int TestsReplaced, int SetsAdded, int SetsReplaced, int Skipped, int ValuesChanged);

    /// <summary>
    /// QOL round: export / import of clash tests and search sets as files, with link / level reconciliation.
    /// Never throws: failures come back as an error text.
    /// </summary>
    public static class ClashSetupFiles
    {
        /// <summary>Format names.</summary>
        public const string TESTS_FORMAT = "bimgo-clash-tests", SETS_FORMAT = "bimgo-clash-sets";

        /// <summary>The shape's version.</summary>
        public const int VERSION = 1;

        /// <summary>Extension and dialog filters.</summary>
        public const string EXTENSION = ".json";

        /// <inheritdoc cref="EXTENSION"/>
        public const string TESTS_FILTER = "BimGo clash tests (*.json)|*.json|All files (*.*)|*.*";

        /// <inheritdoc cref="EXTENSION"/>
        public const string SETS_FILTER = "BimGo search sets (*.json)|*.json|All files (*.*)|*.*";

        /// <summary><see cref="ClashSetupMissing.Replacement"/> that takes the value out of its rules.</summary>
        public const string DROP = "\u0000drop";

        #region Export

        /// <summary>
        /// Builds the file: every test with the sets it uses (<see cref="ClashSetupKind.Tests"/>) or every set, plus the
        /// names of the links their rules name.
        /// </summary>
        public static ClashSetupFile Build(ClashDocument document, ClashSetupKind kind, IReadOnlyList<LinkInfo> links, string modelTitle, string generator)
        {
            var file = new ClashSetupFile
            {
                Format = kind == ClashSetupKind.Tests ? TESTS_FORMAT : SETS_FORMAT,
                ModelTitle = modelTitle,
                Generator = generator,
                ExportedUtc = DateTime.UtcNow
            };
            if (document == null) { return file; }

            if (kind == ClashSetupKind.Tests)
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (ClashTest test in document.Tests)
                {
                    file.Tests.Add(CopyTest(test, keepRun: false));
                    if (!string.IsNullOrEmpty(test.SetA)) { used.Add(test.SetA); }
                    if (!string.IsNullOrEmpty(test.SetB)) { used.Add(test.SetB); }
                }
                foreach (SearchSet set in document.Sets)
                {
                    if (used.Contains(set.Id)) { file.Sets.Add(set.Copy()); }
                }
            }
            else
            {
                foreach (SearchSet set in document.Sets) { file.Sets.Add(set.Copy()); }
            }

            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SearchSet set in file.Sets)
            {
                set.NormaliseRules();
                foreach (SetRule rule in set.AllRules())
                {
                    if (rule.Field != RuleField.LINK) { continue; }
                    for (int i = 0; i < rule.ValueCount; i++)
                    {
                        string value = rule.ValueAt(i);
                        if (value.Length == 0 || !named.Add(value)) { continue; }
                        LinkInfo link = links?.FirstOrDefault(l => string.Equals(l.InstanceUniqueId, value, StringComparison.OrdinalIgnoreCase));
                        file.Links.Add(new ClashSetupLink { UniqueId = value, Name = link?.Label ?? value });
                    }
                }
            }
            return file;
        }

        /// <summary>The file as readable JSON.</summary>
        public static byte[] Serialize(ClashSetupFile file) => JsonSerializer.SerializeToUtf8Bytes(file, BimGoFormat.JSON_INDENTED);

        /// <summary>
        /// Writes the file (temp file, then replace).
        /// </summary>
        public static bool Export(string path, ClashSetupFile file, out string error)
        {
            error = null;
            try
            {
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, Serialize(file));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                error = $"The file could not be saved: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return false;
            }
        }

        #endregion

        #region Read

        /// <summary>
        /// Reads a tests / sets file (cleaned), or null with a reason. <paramref name="expected"/> must match: a sets
        /// file imported on the TESTS tab (or the other way) is refused with a pointer to the right tab.
        /// </summary>
        public static ClashSetupFile Parse(byte[] json, ClashSetupKind expected, out string error)
        {
            error = null;
            if (json == null || json.Length == 0) { error = "The file is empty."; return null; }
            try
            {
                ClashSetupFile file = JsonSerializer.Deserialize<ClashSetupFile>(json, BimGoFormat.JSON_INDENTED);
                if (file == null) { error = "The file is empty."; return null; }
                bool tests = string.Equals(file.Format, TESTS_FORMAT, StringComparison.OrdinalIgnoreCase);
                bool sets = string.Equals(file.Format, SETS_FORMAT, StringComparison.OrdinalIgnoreCase);
                if (!tests && !sets)
                {
                    error = string.Equals(file.Format, ClashFiles.REPORT_FORMAT, StringComparison.OrdinalIgnoreCase)
                        ? "This is a clash report: import it from clash mode (REPORT… → IMPORT REPORT…)."
                        : "This is not a BimGo tests or sets file.";
                    return null;
                }
                if (file.Kind != expected)
                {
                    error = file.Kind == ClashSetupKind.Tests ? "This is a tests file: import it on the TESTS tab." : "This is a sets file: import it on the SETS tab.";
                    return null;
                }
                if (file.Version > VERSION)
                {
                    error = $"This file was made by a newer BimGo (version {file.Version}). Please update BimGo.";
                    return null;
                }
                Clean(file);
                return file;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                error = $"The file could not be read: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return null;
            }
        }

        /// <summary>
        /// Reads a file from disk (as <see cref="Parse"/>).
        /// </summary>
        public static ClashSetupFile Read(string path, ClashSetupKind expected, out string error)
        {
            try { return Parse(File.ReadAllBytes(path), expected, out error); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                error = $"The file could not be read: {ex.Message}";
                return null;
            }
        }

        /// <summary>Null lists and entries out, ids filled in, rules normalised, references to absent sets cleared.</summary>
        private static void Clean(ClashSetupFile file)
        {
            file.Sets = (file.Sets ?? new List<SearchSet>()).Where(s => s != null).ToList();
            file.Tests = (file.Tests ?? new List<ClashTest>()).Where(t => t != null).ToList();
            file.Links = (file.Links ?? new List<ClashSetupLink>()).Where(l => l != null && !string.IsNullOrEmpty(l.UniqueId)).ToList();
            foreach (SearchSet set in file.Sets)
            {
                if (string.IsNullOrWhiteSpace(set.Id)) { set.Id = Guid.NewGuid().ToString("N"); }
                set.Name = string.IsNullOrWhiteSpace(set.Name) ? "Imported set" : set.Name.Trim();
                set.NormaliseRules();
            }
            var ids = new HashSet<string>(file.Sets.Select(s => s.Id), StringComparer.Ordinal);
            foreach (ClashTest test in file.Tests)
            {
                if (string.IsNullOrWhiteSpace(test.Id)) { test.Id = Guid.NewGuid().ToString("N"); }
                test.Name = string.IsNullOrWhiteSpace(test.Name) ? "Imported test" : test.Name.Trim();
                test.Kind = ClashKind.Normalise(test.Kind);
                if (!double.IsFinite(test.Tolerance) || test.Tolerance < 0) { test.Tolerance = 0; }
                if (test.SetA != null && !ids.Contains(test.SetA)) { test.SetA = null; }
                if (!string.IsNullOrEmpty(test.SetB) && !ids.Contains(test.SetB)) { test.SetB = null; }
            }
        }

        #endregion

        #region Plan

        /// <summary>
        /// What importing <paramref name="file"/> into <paramref name="into"/> will meet: names already taken (tests in
        /// a tests file, sets in a sets file) and link / level values this model doesn't have. A missing link whose
        /// name (from the file) matches a link here is pre-mapped to it.
        /// </summary>
        public static ClashSetupPlan Plan(ClashSetupFile file, ClashDocument into, IReadOnlyList<LinkInfo> links, IEnumerable<string> levels)
        {
            var plan = new ClashSetupPlan { File = file };
            if (file == null || into == null) { return plan; }

            if (file.Kind == ClashSetupKind.Tests)
            {
                var taken = new HashSet<string>(into.Tests.Select(t => t.Name), StringComparer.CurrentCultureIgnoreCase);
                plan.NameClashes.AddRange(file.Tests.Where(t => taken.Contains(t.Name)).Select(t => t.Name));
            }
            else
            {
                var taken = new HashSet<string>(into.Sets.Select(s => s.Name), StringComparer.CurrentCultureIgnoreCase);
                plan.NameClashes.AddRange(file.Sets.Where(s => taken.Contains(s.Name)).Select(s => s.Name));
            }

            var levelNames = new HashSet<string>(levels ?? Enumerable.Empty<string>(), StringComparer.CurrentCultureIgnoreCase);
            var found = new Dictionary<(string, string), ClashSetupMissing>();
            foreach (SetRule rule in file.Sets.SelectMany(s => s.AllRules()))
            {
                if (rule.Field != RuleField.LINK && rule.Field != RuleField.LEVEL) { continue; }
                for (int i = 0; i < rule.ValueCount; i++)
                {
                    string value = rule.ValueAt(i);
                    if (value.Length == 0) { continue; } // link "" = the host model; level "" never set
                    bool known = rule.Field == RuleField.LINK ? FindLink(links, value) != null : levelNames.Contains(value);
                    if (known) { continue; }
                    var key = (rule.Field, value.ToLowerInvariant());
                    if (found.TryGetValue(key, out ClashSetupMissing missing))
                    {
                        missing.Uses++;
                        continue;
                    }
                    missing = new ClashSetupMissing { Field = rule.Field, Value = value, Label = value, Uses = 1 };
                    if (rule.Field == RuleField.LINK)
                    {
                        string name = file.Links.FirstOrDefault(l => string.Equals(l.UniqueId, value, StringComparison.OrdinalIgnoreCase))?.Name;
                        if (!string.IsNullOrEmpty(name)) { missing.Label = name; }
                        LinkInfo byName = name == null ? null : FindLink(links, name);
                        if (byName != null)
                        {
                            missing.Replacement = byName.InstanceUniqueId;
                            missing.AutoMatched = true;
                        }
                    }
                    found[key] = missing;
                    plan.Missing.Add(missing);
                }
            }
            plan.Missing.Sort((a, b) => a.Field != b.Field ? string.CompareOrdinal(b.Field, a.Field) // links first
                : string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
            return plan;
        }

        /// <summary>The link a rule value names here (UniqueId, then name or label, case ignored), or null.</summary>
        public static LinkInfo FindLink(IReadOnlyList<LinkInfo> links, string value)
        {
            if (links == null || string.IsNullOrEmpty(value)) { return null; }
            foreach (LinkInfo link in links)
            {
                if (string.Equals(link.InstanceUniqueId, value, StringComparison.OrdinalIgnoreCase)) { return link; }
            }
            foreach (LinkInfo link in links)
            {
                if (string.Equals(link.Name, value, StringComparison.OrdinalIgnoreCase) || string.Equals(link.Label, value, StringComparison.OrdinalIgnoreCase)) { return link; }
            }
            return null;
        }

        #endregion

        #region Apply

        /// <summary>
        /// Imports the planned file: values replaced as <see cref="ClashSetupPlan.Missing"/> says, then (sets file) each
        /// set added or, when its name is taken, replaced / kept as a copy / skipped; (tests file) each test likewise,
        /// and the sets it uses found by id or name here, else added.
        /// </summary>
        public static ClashSetupResult Apply(ClashSetupPlan plan, ClashDocument into)
        {
            if (plan?.File == null || into == null) { return default; }
            ClashSetupFile file = plan.File;
            int changed = Reconcile(file, plan.Missing);
            int testsAdded = 0, testsReplaced = 0, setsAdded = 0, setsReplaced = 0, skipped = 0;

            if (file.Kind == ClashSetupKind.Sets)
            {
                foreach (SearchSet incoming in file.Sets)
                {
                    SearchSet existing = into.Sets.FirstOrDefault(s => string.Equals(s.Name, incoming.Name, StringComparison.CurrentCultureIgnoreCase));
                    if (existing == null)
                    {
                        into.Sets.Add(Fresh(incoming, into));
                        setsAdded++;
                        continue;
                    }
                    switch (plan.OnNameClash)
                    {
                        case ClashNameClash.Replace:
                            existing.MatchAll = incoming.MatchAll;
                            existing.Rules = incoming.Rules.Select(r => r.Copy()).ToList();
                            existing.Groups = incoming.Groups?.Select(g => g.Copy()).ToList();
                            setsReplaced++;
                            break;
                        case ClashNameClash.KeepBoth:
                            SearchSet copy = Fresh(incoming, into);
                            copy.Name = UniqueName(incoming.Name, into.Sets.Select(s => s.Name));
                            into.Sets.Add(copy);
                            setsAdded++;
                            break;
                        default:
                            skipped++;
                            break;
                    }
                }
                return new ClashSetupResult(0, 0, setsAdded, setsReplaced, skipped, changed);
            }

            // Tests: the sets they use, matched here by id, then by name; else added
            var setMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SearchSet incoming in file.Sets)
            {
                SearchSet here = into.FindSet(incoming.Id)
                    ?? into.Sets.FirstOrDefault(s => string.Equals(s.Name, incoming.Name, StringComparison.CurrentCultureIgnoreCase));
                if (here == null)
                {
                    here = Fresh(incoming, into);
                    into.Sets.Add(here);
                    setsAdded++;
                }
                setMap[incoming.Id] = here.Id;
            }

            foreach (ClashTest incoming in file.Tests)
            {
                string setA = incoming.SetA != null && setMap.TryGetValue(incoming.SetA, out string a) ? a : null;
                string setB = !string.IsNullOrEmpty(incoming.SetB) && setMap.TryGetValue(incoming.SetB, out string b) ? b : null;
                ClashTest existing = into.Tests.FirstOrDefault(t => string.Equals(t.Name, incoming.Name, StringComparison.CurrentCultureIgnoreCase));
                if (existing != null && plan.OnNameClash == ClashNameClash.Skip)
                {
                    skipped++;
                    continue;
                }
                if (existing != null && plan.OnNameClash == ClashNameClash.Replace)
                {
                    // The id stays (its clashes stay with it); the next run says "model changed since"
                    existing.Kind = incoming.Kind;
                    existing.SetA = setA;
                    existing.SetB = setB;
                    existing.Tolerance = incoming.Tolerance;
                    existing.Enabled = incoming.Enabled;
                    existing.LastRunModel = null;
                    testsReplaced++;
                    continue;
                }
                ClashTest test = CopyTest(incoming, keepRun: false);
                test.Id = into.FindTest(incoming.Id) == null ? incoming.Id : Guid.NewGuid().ToString("N");
                test.SetA = setA;
                test.SetB = setB;
                if (existing != null) { test.Name = UniqueName(incoming.Name, into.Tests.Select(t => t.Name)); }
                into.Tests.Add(test);
                testsAdded++;
            }
            return new ClashSetupResult(testsAdded, testsReplaced, setsAdded, setsReplaced, skipped, changed);
        }

        /// <summary>
        /// Replaces (or drops) the planned values in the file's rules.
        /// </summary>
        /// <returns>The number of values changed.</returns>
        public static int Reconcile(ClashSetupFile file, IReadOnlyList<ClashSetupMissing> missing)
        {
            if (file == null || missing == null || missing.Count == 0) { return 0; }
            int changed = 0;
            foreach (SetRule rule in file.Sets.SelectMany(s => s.AllRules()))
            {
                if (rule.Field != RuleField.LINK && rule.Field != RuleField.LEVEL) { continue; }
                rule.Normalise();
                bool any = false;
                var values = new List<string>(rule.Values.Count);
                foreach (string value in rule.Values)
                {
                    ClashSetupMissing m = missing.FirstOrDefault(x => x.Field == rule.Field && string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
                    if (m == null || m.Replacement == null) { values.Add(value); continue; }
                    any = true;
                    changed++;
                    if (m.Replacement != DROP && !values.Contains(m.Replacement, StringComparer.OrdinalIgnoreCase)) { values.Add(m.Replacement); }
                }
                if (any) { rule.SetValues(values); }
            }
            return changed;
        }

        /// <summary>A copy of an imported set for this document (a new id when its id is taken here).</summary>
        private static SearchSet Fresh(SearchSet incoming, ClashDocument into) => incoming.Copy(newId: into.FindSet(incoming.Id) != null);

        /// <summary>"Name (2)", "Name (3)"… the first not taken.</summary>
        public static string UniqueName(string name, IEnumerable<string> taken)
        {
            var names = new HashSet<string>(taken ?? Enumerable.Empty<string>(), StringComparer.CurrentCultureIgnoreCase);
            if (!names.Contains(name)) { return name; }
            for (int n = 2; ; n++)
            {
                string candidate = $"{name} ({n})";
                if (!names.Contains(candidate)) { return candidate; }
            }
        }

        /// <summary>A test's definition (run state only when <paramref name="keepRun"/>).</summary>
        private static ClashTest CopyTest(ClashTest test, bool keepRun) => new()
        {
            Id = test.Id,
            Name = test.Name,
            Kind = test.Kind,
            SetA = test.SetA,
            SetB = test.SetB,
            Tolerance = test.Tolerance,
            Enabled = test.Enabled,
            LastRunUtc = keepRun ? test.LastRunUtc : null,
            LastRunModel = keepRun ? test.LastRunModel : null,
            LastRunTruncated = keepRun && test.LastRunTruncated
        };

        #endregion
    }
}
