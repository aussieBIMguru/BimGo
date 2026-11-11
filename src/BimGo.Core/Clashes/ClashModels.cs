using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Clashes namespace
namespace BimGo.Clashes
{
    /// <summary>
    /// Clash statuses (Round C), stored as words so files stay readable and older readers keep unknown values.
    /// A clash found for the first time is <see cref="NEW"/>; found again it becomes <see cref="ACTIVE"/>; people move
    /// it to <see cref="REVIEWED"/> or <see cref="APPROVED"/>; a clash missing from a later complete run becomes
    /// <see cref="RESOLVED"/> (and goes back to active if it reappears).
    /// </summary>
    public static class ClashStatus
    {
        public const string NEW = "new";
        public const string ACTIVE = "active";
        public const string REVIEWED = "reviewed";
        public const string APPROVED = "approved";
        public const string RESOLVED = "resolved";

        /// <summary>Every status in display order.</summary>
        public static readonly string[] All = { NEW, ACTIVE, REVIEWED, APPROVED, RESOLVED };

        /// <summary>A known status for any text (unknown or empty reads as new).</summary>
        public static string Normalise(string status)
        {
            foreach (string known in All)
            {
                if (string.Equals(known, status?.Trim(), StringComparison.OrdinalIgnoreCase)) { return known; }
            }
            return NEW;
        }

        /// <summary>The index of a status in <see cref="All"/> (0 for unknown).</summary>
        public static int IndexOf(string status) => Math.Max(0, Array.IndexOf(All, Normalise(status)));

        /// <summary>A label for people ("Reviewed").</summary>
        public static string Label(string status)
        {
            string s = Normalise(status);
            return char.ToUpperInvariant(s[0]) + s[1..];
        }
    }

    /// <summary>
    /// The two kinds of test.
    /// </summary>
    public static class ClashKind
    {
        /// <summary>The two elements' surfaces cross, deeper than the tolerance.</summary>
        public const string HARD = "hard";

        /// <summary>The two elements come closer than the distance (crossing included, marked as such).</summary>
        public const string CLEARANCE = "clearance";

        /// <summary>A known kind for any text (unknown reads as hard).</summary>
        public static string Normalise(string kind) =>
            string.Equals(kind?.Trim(), CLEARANCE, StringComparison.OrdinalIgnoreCase) ? CLEARANCE : HARD;
    }

    /// <summary>
    /// Clash → comment (Round C-B2: TO COMMENT / TO COMMENTS): how a clash's review state reads on its comment. The
    /// comment follows the clash one way (a clash's status change updates its comment, never the other way round).
    /// </summary>
    public static class ClashCommentRules
    {
        /// <summary>A hard clash crossing deeper than this (m) makes a high-priority comment.</summary>
        public const double HIGH_PRIORITY_DEPTH = 0.05;

        /// <summary>The most clashes TO COMMENTS makes comments for in one go.</summary>
        public const int BATCH_LIMIT = 500;

        /// <summary>
        /// The comment status for a clash status: new / active → open, reviewed → in progress, approved / resolved →
        /// closed.
        /// </summary>
        public static string CommentStatusFor(string clashStatus) => ClashStatus.Normalise(clashStatus) switch
        {
            ClashStatus.REVIEWED => BimGo.Format.CommentStatus.IN_PROGRESS,
            ClashStatus.APPROVED or ClashStatus.RESOLVED => BimGo.Format.CommentStatus.CLOSED,
            _ => BimGo.Format.CommentStatus.OPEN
        };

        /// <summary>The comment priority: high for a hard clash crossing deeper than 50 mm, else normal.</summary>
        public static string PriorityFor(ClashRecord clash, string testKind) =>
            clash != null && ClashKind.Normalise(testKind) == ClashKind.HARD && clash.Crossing && clash.Distance > HIGH_PRIORITY_DEPTH
                ? BimGo.Format.CommentPriority.HIGH
                : BimGo.Format.CommentPriority.NORMAL;
    }

    /// <summary>
    /// What a search set rule looks at.
    /// </summary>
    public static class RuleField
    {
        /// <summary>BimGo's category group key (<c>CategoryCatalog</c>: "ducts", "framing"…): the same in every Revit language.</summary>
        public const string CATALOG = "catalog";

        /// <summary>The Revit category name as extracted ("Duct Fittings"; language of the Revit that exported it).</summary>
        public const string CATEGORY = "category";

        /// <summary>The family name (the part of "Family: Type" before the colon; empty for system families).</summary>
        public const string FAMILY = "family";

        /// <summary>The type name (the part after the colon, or the whole name for system families).</summary>
        public const string TYPE = "type";

        /// <summary>The level name.</summary>
        public const string LEVEL = "level";

        /// <summary>The model: empty = the host model, else the link instance's UniqueId (or its name).</summary>
        public const string LINK = "link";

        /// <summary>
        /// Colour round A: the element's phase role (<see cref="PhaseValues"/>: "existing", "new", "between", "unphased").
        /// </summary>
        public const string PHASE = "phase";

        /// <summary>
        /// Colour round A: one of the extra parameters picked in Revit Options → Parameters (the rule's
        /// <see cref="SetRule.Parameter"/> names it; instance value, else the type's, as display text).
        /// </summary>
        public const string PARAMETER = "parameter";

        /// <summary>The built-in fields in display order (parameters follow them, one per extracted name).</summary>
        public static readonly string[] All = { CATALOG, CATEGORY, FAMILY, TYPE, LEVEL, LINK, PHASE };

        /// <summary>The stored phase values, in <see cref="BimGo.Scene.PhaseRole"/> order.</summary>
        public static readonly string[] PhaseValues = { "existing", "new", "between", "unphased" };

        /// <summary>Labels of <see cref="PhaseValues"/>.</summary>
        public static readonly string[] PhaseLabels = { "Existing", "New", "Between phases", "Not phased" };

        /// <summary>A known field for any text (unknown reads as category).</summary>
        public static string Normalise(string field)
        {
            foreach (string known in All)
            {
                if (string.Equals(known, field?.Trim(), StringComparison.OrdinalIgnoreCase)) { return known; }
            }
            if (string.Equals(PARAMETER, field?.Trim(), StringComparison.OrdinalIgnoreCase)) { return PARAMETER; }
            return CATEGORY;
        }

        /// <summary>The stored value of a phase role.</summary>
        public static string PhaseValue(BimGo.Scene.PhaseRole role) => PhaseValues[Math.Clamp((int)role, 0, PhaseValues.Length - 1)];

        /// <summary>The label of a stored phase value (the value itself when unknown).</summary>
        public static string PhaseLabel(string value)
        {
            for (int i = 0; i < PhaseValues.Length; i++)
            {
                if (string.Equals(PhaseValues[i], value, StringComparison.OrdinalIgnoreCase)) { return PhaseLabels[i]; }
            }
            return value ?? string.Empty;
        }
    }

    /// <summary>
    /// How a rule compares (C-B1 fixes): stored as words. The "not" operators always take matching elements out of the
    /// set, whatever the other rules say (they replace the earlier exclude switch); the others add elements (any rule,
    /// or all rules, per <see cref="SearchSet.MatchAll"/>). All comparisons ignore case.
    /// </summary>
    public static class RuleOp
    {
        /// <summary>The field equals the value.</summary>
        public const string IS = "is";

        /// <summary>The field equals one of the values.</summary>
        public const string IS_ONE_OF = "isOneOf";

        /// <summary>Takes out elements whose field equals the value.</summary>
        public const string IS_NOT = "isNot";

        /// <summary>Takes out elements whose field equals any of the values.</summary>
        public const string IS_NOT_ANY_OF = "isNotAnyOf";

        /// <summary>The field contains the text.</summary>
        public const string CONTAINS = "contains";

        /// <summary>Takes out elements whose field contains the text.</summary>
        public const string NOT_CONTAINS = "notContains";

        /// <summary>
        /// Colour round B: the field's number (the first number in its text, units ignored: "600 mm" → 600) is at
        /// least the value. Elements without a number don't match.
        /// </summary>
        public const string AT_LEAST = "atLeast";

        /// <summary>Colour round B: the field's number is at most the value.</summary>
        public const string AT_MOST = "atMost";

        /// <summary>Colour round B: the field's number is between the two values (inclusive, either order).</summary>
        public const string BETWEEN = "between";

        /// <summary>Every operator in display order.</summary>
        public static readonly string[] All = { IS, IS_ONE_OF, IS_NOT, IS_NOT_ANY_OF, CONTAINS, NOT_CONTAINS, AT_LEAST, AT_MOST, BETWEEN };

        /// <summary>Labels for buttons and lists (<see cref="All"/> order).</summary>
        public static readonly string[] Labels = { "IS", "IS ONE OF", "IS NOT", "IS NOT ANY OF", "CONTAINS", "DOES NOT CONTAIN", "AT LEAST (≥)", "AT MOST (≤)", "BETWEEN" };

        /// <summary>The index of an operator in <see cref="All"/>, or −1.</summary>
        public static int IndexOf(string op)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i], op?.Trim(), StringComparison.OrdinalIgnoreCase)) { return i; }
            }
            return -1;
        }

        /// <summary>A known operator for any text (unknown reads as <see cref="IS"/>).</summary>
        public static string Normalise(string op)
        {
            int index = IndexOf(op);
            return index < 0 ? IS : All[index];
        }

        /// <summary>True when matching elements are taken out of the set.</summary>
        public static bool IsNegative(string op) => op is IS_NOT or IS_NOT_ANY_OF or NOT_CONTAINS;

        /// <summary>True when the rule holds a list of values (picked with ticks).</summary>
        public static bool IsList(string op) => op is IS_ONE_OF or IS_NOT_ANY_OF;

        /// <summary>True when the value is typed text (part of a name), not a value picked from the model.</summary>
        public static bool IsText(string op) => op is CONTAINS or NOT_CONTAINS;

        /// <summary>Colour round B: true for the number operators (at least, at most, between: typed numbers).</summary>
        public static bool IsNumeric(string op) => op is AT_LEAST or AT_MOST or BETWEEN;

        /// <summary>How many values the operator keeps (between: 2; the list operators: any).</summary>
        public static int MaxValues(string op) => IsList(op) ? int.MaxValue : op == BETWEEN ? 2 : 1;

        /// <summary>A lower-case label for descriptions ("is not any of", "at least").</summary>
        public static string Words(string op) => op switch
        {
            AT_LEAST => "at least",
            AT_MOST => "at most",
            _ => Labels[Math.Max(0, IndexOf(op))].ToLowerInvariant()
        };
    }

    /// <summary>
    /// One rule of a search set: a field, an operator (<see cref="RuleOp"/>) and its values. A rule with no value yet
    /// (just added, or a "contains" with no text) is left out of matching until it has one. The host model (link
    /// field) and system families (family field) are the empty value, so an empty string in <see cref="Values"/> is a
    /// value, not "none".
    /// </summary>
    public sealed class SetRule
    {
        /// <summary>The field (<see cref="RuleField"/>).</summary>
        public string Field { get; set; } = RuleField.CATALOG;

        /// <summary>
        /// Colour round A: the parameter's name when <see cref="Field"/> is <see cref="RuleField.PARAMETER"/>, else null
        /// (not written).
        /// </summary>
        public string Parameter { get; set; }

        /// <summary>The operator (<see cref="RuleOp"/>; unknown or missing reads as <see cref="RuleOp.IS"/>).</summary>
        public string Op { get; set; }

        /// <summary>The values (one for is / is not / contains / does not contain; any number for the list operators).</summary>
        public List<string> Values { get; set; } = new();

        /// <summary>The operator in effect.</summary>
        [JsonIgnore]
        public string Operator => RuleOp.Normalise(Op);

        /// <summary>How many values the rule compares with.</summary>
        [JsonIgnore]
        public int ValueCount => Values?.Count ?? 0;

        /// <summary>One of the values (no allocation).</summary>
        public string ValueAt(int index) => Values[index] ?? string.Empty;

        /// <summary>
        /// True when the rule takes part in matching: it has a value (a text operator: some text).
        /// </summary>
        [JsonIgnore]
        public bool IsComplete
        {
            get
            {
                int count = ValueCount;
                if (count == 0) { return false; }
                if (RuleOp.IsNumeric(Operator))
                {
                    // Colour round B: numbers only (between needs both)
                    int needed = Operator == RuleOp.BETWEEN ? 2 : 1;
                    if (count < needed) { return false; }
                    for (int i = 0; i < needed; i++)
                    {
                        if (!RuleNumbers.TryParse(ValueAt(i), out _)) { return false; }
                    }
                    return true;
                }
                if (!RuleOp.IsText(Operator)) { return true; }
                for (int i = 0; i < count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(ValueAt(i))) { return true; }
                }
                return false;
            }
        }

        /// <summary>
        /// Makes the rule explicit (a known operator, no null values) and trims a single-value operator to one value.
        /// Returns itself.
        /// </summary>
        public SetRule Normalise()
        {
            Field = RuleField.Normalise(Field);
            Parameter = Field == RuleField.PARAMETER ? (Parameter ?? string.Empty).Trim() : null;
            Op = RuleOp.Normalise(Op);
            Values = (Values ?? new List<string>()).Select(v => v ?? string.Empty).ToList();
            int keep = RuleOp.MaxValues(Op);
            if (Values.Count > keep) { Values.RemoveRange(keep, Values.Count - keep); }
            return this;
        }

        /// <summary>
        /// Changes the operator: a list keeps its values; a single-value operator keeps the first one.
        /// </summary>
        public void SetOperator(string op)
        {
            Normalise();
            bool wasNumeric = RuleOp.IsNumeric(Op);
            Op = RuleOp.Normalise(op);
            if (wasNumeric != RuleOp.IsNumeric(Op)) { Values = new List<string>(); } // colour round B: numbers and names don't mix
            Normalise();
        }

        /// <summary>Replaces the values (single-value operators keep the first).</summary>
        public void SetValues(IEnumerable<string> values)
        {
            Normalise();
            Values = values?.Select(v => v ?? string.Empty).ToList() ?? new List<string>();
            Normalise();
        }

        /// <summary>Changes the field (the values belong to the old field, so they go).</summary>
        /// <param name="field">The field (<see cref="RuleField"/>).</param>
        /// <param name="parameter">The parameter's name, for <see cref="RuleField.PARAMETER"/>.</param>
        public void SetField(string field, string parameter = null)
        {
            Normalise();
            Field = RuleField.Normalise(field);
            Parameter = parameter;
            Values = new List<string>();
            Normalise();
        }

        /// <summary>True when the rule looks at the same field (and parameter) as another field choice.</summary>
        public bool IsField(string field, string parameter)
        {
            string normal = RuleField.Normalise(field);
            if (RuleField.Normalise(Field) != normal) { return false; }
            return normal != RuleField.PARAMETER || string.Equals(Parameter ?? string.Empty, parameter ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the rule holds a value (case ignored).</summary>
        public bool HasValue(string value)
        {
            for (int i = 0; i < ValueCount; i++)
            {
                if (string.Equals(ValueAt(i), value ?? string.Empty, StringComparison.CurrentCultureIgnoreCase)) { return true; }
            }
            return false;
        }

        /// <summary>List operators: adds the value, or takes it out when it is there.</summary>
        public void ToggleValue(string value)
        {
            Normalise();
            value ??= string.Empty;
            int index = Values.FindIndex(v => string.Equals(v, value, StringComparison.CurrentCultureIgnoreCase));
            if (index >= 0) { Values.RemoveAt(index); }
            else { Values.Add(value); }
            Normalise();
        }

        /// <summary>A copy.</summary>
        public SetRule Copy() => new()
        {
            Field = Field,
            Parameter = Parameter,
            Op = Op,
            Values = Values == null ? new List<string>() : new List<string>(Values)
        };
    }

    /// <summary>
    /// A named group of elements chosen by rules (Round C, stage 1: category / family / type / level / model).
    /// An element is in the set when it matches the adding rules (any of them, or all of them with
    /// <see cref="MatchAll"/>) and no "not" rule (<see cref="RuleOp.IsNegative"/>). A set with no adding rule holds
    /// every element (minus the "not" rules). Rules with no value yet are left out.
    /// <para>C-B4: a set can also hold <see cref="Groups"/> (Revit-filter style AND / OR, one level deep): each group
    /// is one more condition, combined with the set's adding rules by <see cref="MatchAll"/>. Inside a group every rule
    /// is an ordinary condition ("Level is not Level 2" is just false for Level 2 elements); the set's own top-level
    /// "not" rules still take elements out of the whole set, as before.</para>
    /// </summary>
    public sealed class SearchSet
    {
        /// <summary>Stable id (tests refer to sets by id).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The name people see.</summary>
        public string Name { get; set; } = "New set";

        /// <summary>True: every adding rule must match; false: any one of them.</summary>
        public bool MatchAll { get; set; }

        /// <summary>The rules (adding ones and "not" ones).</summary>
        public List<SetRule> Rules { get; set; } = new();

        /// <summary>C-B4: rule groups (null or empty: none, the file stays as before).</summary>
        public List<RuleGroup> Groups { get; set; }

        /// <summary>Every rule: the set's own, then each group's (polish round P-C: nested groups' too, depth first).</summary>
        public IEnumerable<SetRule> AllRules()
        {
            foreach (SetRule rule in Rules ?? Enumerable.Empty<SetRule>()) { yield return rule; }
            foreach (RuleGroup group in Groups ?? Enumerable.Empty<RuleGroup>())
            {
                if (group == null) { continue; }
                foreach (SetRule rule in group.AllRules()) { yield return rule; }
            }
        }

        /// <summary>Drops null rules / groups, normalises every rule, and leaves <see cref="Groups"/> null when empty.</summary>
        public void NormaliseRules()
        {
            // In place (the editor holds these lists)
            Rules ??= new List<SetRule>();
            Rules.RemoveAll(r => r == null);
            foreach (SetRule rule in Rules) { rule.Normalise(); }
            Groups?.RemoveAll(g => g == null);
            foreach (RuleGroup group in Groups ?? Enumerable.Empty<RuleGroup>()) { group.Normalise(); }
            if (Groups != null && Groups.Count == 0) { Groups = null; }
        }

        /// <summary>A copy (new id when <paramref name="newId"/>).</summary>
        public SearchSet Copy(bool newId = false) => new()
        {
            Id = newId ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            MatchAll = MatchAll,
            Rules = Rules?.Where(r => r != null).Select(r => r.Copy()).ToList() ?? new List<SetRule>(),
            Groups = Groups?.Where(g => g != null).Select(g => g.Copy()).ToList()
        };
    }

    /// <summary>
    /// Colour round B: numbers in rule values and element texts. The first number in a text counts ("600 mm" → 600,
    /// "-/120/120" → 120, "5' 7\"" → 5); spaces, apostrophes and thin spaces between digit groups are dropped; a comma
    /// is a thousands separator when three digits follow it and there is no other decimal mark ("1,200"), otherwise
    /// a decimal mark ("2,5"). Values are compared as shown (the project's display units).
    /// </summary>
    public static class RuleNumbers
    {
        /// <summary>The first number in a text, or false when there is none.</summary>
        public static bool TryParse(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            int i = 0;
            while (i < text.Length && !char.IsDigit(text[i])) { i++; }
            if (i >= text.Length) { return false; }
            bool negative = i > 0 && (text[i - 1] == '-' || text[i - 1] == '−') && (i < 2 || !char.IsDigit(text[i - 2]));
            if (i > 0 && text[i - 1] == '.') { i--; } // ".5"

            var digits = new System.Text.StringBuilder();
            bool decimalSeen = false;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsDigit(c)) { digits.Append(c); continue; }
                bool nextDigit = i + 1 < text.Length && char.IsDigit(text[i + 1]);
                if (!nextDigit) { break; }
                if ((c == ' ' || c == '\u00A0' || c == '\u202F' || c == '\'') && digits.Length > 0 && !decimalSeen && GroupFollows(text, i + 1)) { continue; }
                if (c == '.' && !decimalSeen)
                {
                    decimalSeen = true;
                    digits.Append('.');
                    continue;
                }
                if (c == ',' && !decimalSeen)
                {
                    if (GroupFollows(text, i + 1)) { continue; } // thousands
                    decimalSeen = true;
                    digits.Append('.');
                    continue;
                }
                break;
            }
            if (digits.Length == 0 || digits[0] == '.' && digits.Length == 1) { return false; }
            if (!double.TryParse(digits.ToString(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out value)) { return false; }
            if (negative) { value = -value; }
            return double.IsFinite(value);
        }

        /// <summary>True when exactly three digits follow (then not a digit): a thousands group.</summary>
        private static bool GroupFollows(string text, int start)
        {
            if (start + 3 > text.Length) { return false; }
            for (int k = start; k < start + 3; k++)
            {
                if (!char.IsDigit(text[k])) { return false; }
            }
            return start + 3 == text.Length || !char.IsDigit(text[start + 3]);
        }

        /// <summary>The numbers in a typed text ("300 to 600" → 300, 600), at most <paramref name="max"/>.</summary>
        public static List<double> All(string text, int max = 2)
        {
            var result = new List<double>();
            string rest = text ?? string.Empty;
            while (result.Count < max && TryParseAt(rest, out double value, out int end))
            {
                result.Add(value);
                rest = rest[end..];
                // "300-600" / "300–600": a dash straight after a number is a range, not a minus
                if (rest.Length > 0 && (rest[0] == '-' || rest[0] == '–')) { rest = rest[1..]; }
            }
            return result;
        }

        private static bool TryParseAt(string text, out double value, out int end)
        {
            end = 0;
            value = 0;
            int i = 0;
            while (i < text.Length && !char.IsDigit(text[i])) { i++; }
            if (i >= text.Length) { return false; }
            int j = i;
            while (j < text.Length && (char.IsDigit(text[j]) || ((text[j] == '.' || text[j] == ',') && j + 1 < text.Length && char.IsDigit(text[j + 1])))) { j++; }
            // A minus straight before (not "300-600", which is a range)
            int from = i > 0 && (text[i - 1] == '-' || text[i - 1] == '−') && (i < 2 || text[i - 2] == ' ') ? i - 1 : i;
            end = j;
            return TryParse(text[from..j], out value);
        }

        /// <summary>A number as rules store it (invariant, up to 6 decimals, no trailing zeros).</summary>
        public static string Format(double value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// C-B4: a group of rules inside a search set: ANY or ALL of its rules (each an ordinary condition, "not" ones
    /// included). Rules with no value yet are left out; a group with none is left out of its set.
    /// </summary>
    public sealed class RuleGroup
    {
        /// <summary>True: every rule must match; false: any one of them.</summary>
        public bool MatchAll { get; set; }

        /// <summary>The rules.</summary>
        public List<SetRule> Rules { get; set; } = new();

        /// <summary>
        /// Polish round P-C: groups inside this group, to any depth (each is one more condition of this group, like a
        /// rule). Null or empty: none, and nothing is written (files from before read as they were).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<RuleGroup> Groups { get; set; }

        /// <summary>Deepest nesting kept when reading (a guard against damaged files; the editor never gets near it).</summary>
        public const int MAX_DEPTH = 16;

        /// <summary>True when at least one rule here, or in a group inside, has a value.</summary>
        public bool HasConditions => (Rules != null && Rules.Any(r => r != null && r.IsComplete))
            || (Groups != null && Groups.Any(g => g != null && g.HasConditions));

        /// <summary>Every rule: this group's, then its groups' (depth first).</summary>
        public IEnumerable<SetRule> AllRules()
        {
            foreach (SetRule rule in Rules ?? Enumerable.Empty<SetRule>()) { yield return rule; }
            foreach (RuleGroup group in Groups ?? Enumerable.Empty<RuleGroup>())
            {
                if (group == null) { continue; }
                foreach (SetRule rule in group.AllRules()) { yield return rule; }
            }
        }

        /// <summary>
        /// Drops null rules and groups, normalises the rest (P-C: and the groups inside, to <see cref="MAX_DEPTH"/>;
        /// deeper ones are dropped), and leaves <see cref="Groups"/> null when empty.
        /// </summary>
        public RuleGroup Normalise() => Normalise(1);

        private RuleGroup Normalise(int depth)
        {
            Rules ??= new List<SetRule>();
            Rules.RemoveAll(r => r == null);
            foreach (SetRule rule in Rules) { rule.Normalise(); }
            if (Groups != null)
            {
                Groups.RemoveAll(g => g == null);
                if (depth >= MAX_DEPTH) { Groups.Clear(); }
                foreach (RuleGroup group in Groups) { group.Normalise(depth + 1); }
                if (Groups.Count == 0) { Groups = null; }
            }
            return this;
        }

        /// <summary>A copy (P-C: with the groups inside).</summary>
        public RuleGroup Copy() => new()
        {
            MatchAll = MatchAll,
            Rules = Rules?.Where(r => r != null).Select(r => r.Copy()).ToList() ?? new List<SetRule>(),
            Groups = Groups?.Where(g => g != null).Select(g => g.Copy()).ToList() is { Count: > 0 } groups ? groups : null
        };

        /// <summary>Number of rules here and inside.</summary>
        public int RuleCount => AllRules().Count();
    }

    /// <summary>
    /// One clash test: set A against set B (or against itself), hard or clearance, with its tolerance.
    /// </summary>
    public sealed class ClashTest
    {
        /// <summary>Stable id (clashes are matched across runs by test + element pair).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The name people see.</summary>
        public string Name { get; set; } = "New test";

        /// <summary>The kind (<see cref="ClashKind"/>).</summary>
        public string Kind { get; set; } = ClashKind.HARD;

        /// <summary>Set A's id.</summary>
        public string SetA { get; set; }

        /// <summary>
        /// Set B's id, or null / empty to test set A against itself. Stored as picked (C-B1 fixes): picking set A's own
        /// set here is a self test too, and changing set A afterwards never changes set B.
        /// </summary>
        public string SetB { get; set; }

        /// <summary>
        /// Metres. Hard: crossings up to this depth are ignored. Clearance: anything closer than this is reported.
        /// Shown in the display units, always stored in metres.
        /// </summary>
        public double Tolerance { get; set; } = ClashDefaults.HARD_TOLERANCE;

        /// <summary>False leaves the test out of RUN ALL.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>When the test last ran to the end (UTC), or null.</summary>
        public DateTime? LastRunUtc { get; set; }

        /// <summary>The model state at that run (<c>ClashEngine</c> callers build it), to say "model changed since".</summary>
        public string LastRunModel { get; set; }

        /// <summary>True when the last run stopped at the clash limit (missing clashes were then not resolved).</summary>
        public bool LastRunTruncated { get; set; }

        /// <summary>True when set B is set A (or empty).</summary>
        [JsonIgnore]
        public bool IsSelfTest => string.IsNullOrEmpty(SetB) || SetB == SetA;
    }

    /// <summary>
    /// How a clash names one of its elements: stable identity first (UniqueId + link, or a walkthrough clone key),
    /// then what people read.
    /// </summary>
    public sealed class ClashElementRef
    {
        /// <summary>The Revit UniqueId (empty for clones / placements made in the walkthrough).</summary>
        public string UniqueId { get; set; } = string.Empty;

        /// <summary>The ElementId value (only stable within one model copy; a fallback).</summary>
        public long ElementId { get; set; }

        /// <summary>The link instance's UniqueId for a linked element, or empty for the host model.</summary>
        public string Link { get; set; } = string.Empty;

        /// <summary>The walkthrough clone key of a clone / placement (0 for model elements).</summary>
        public int CloneKey { get; set; }

        /// <summary>The IFC GUID when known (BCF components).</summary>
        public string IfcGuid { get; set; }

        /// <summary>The Revit category name.</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>"Family: Type".</summary>
        public string FamilyType { get; set; } = string.Empty;

        /// <summary>The level name.</summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>
        /// The key used to match the element across runs: link + UniqueId (or clone key, or ElementId as a last resort).
        /// </summary>
        [JsonIgnore]
        public string Key => (Link ?? string.Empty) + "/" + (CloneKey > 0 ? "clone:" + CloneKey
            : !string.IsNullOrEmpty(UniqueId) ? UniqueId : "id:" + ElementId);

        /// <summary>A short label for lists ("Ducts · Rectangular Duct: 600x300").</summary>
        [JsonIgnore]
        public string Label => string.IsNullOrWhiteSpace(FamilyType) ? Category : $"{Category} · {FamilyType}";
    }

    /// <summary>
    /// One clash: a test, an element pair, where and how much, and its review state. Points are Revit internal
    /// coordinates in metres (like comments), so they stay put when a refresh moves the scene origin.
    /// </summary>
    public sealed class ClashRecord
    {
        /// <summary>Stable id (kept across runs while the pair keeps clashing).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The test's id.</summary>
        public string TestId { get; set; }

        /// <summary>The element from set A.</summary>
        public ClashElementRef A { get; set; } = new();

        /// <summary>The element from set B (or the other element of a self test).</summary>
        public ClashElementRef B { get; set; } = new();

        /// <summary>The clash point (internal metres).</summary>
        public double X { get; set; }

        /// <inheritdoc cref="X"/>
        public double Y { get; set; }

        /// <inheritdoc cref="X"/>
        public double Z { get; set; }

        /// <summary>
        /// Metres: for a crossing, its estimated depth; for a clearance clash that doesn't cross, the gap.
        /// </summary>
        public double Distance { get; set; }

        /// <summary>True when the surfaces cross (always for hard clashes; "hard" tag on clearance clashes).</summary>
        public bool Crossing { get; set; }

        /// <summary>True when either element is a bounding-box stand-in (the result is approximate).</summary>
        public bool Approximate { get; set; }

        /// <summary>The status (<see cref="ClashStatus"/>).</summary>
        public string Status { get; set; } = ClashStatus.NEW;

        /// <summary>Who it is assigned to (free text), or null.</summary>
        public string AssignedTo { get; set; }

        /// <summary>Notes (free text), or null.</summary>
        public string Notes { get; set; }

        /// <summary>
        /// The comment made from this clash (Round C-B: a comment with the clash view and picture, so EXPORT BCF carries
        /// it), or null.
        /// </summary>
        public string CommentId { get; set; }

        /// <summary>When first found (UTC).</summary>
        public DateTime FoundUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When last found by a run (UTC).</summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When a run last found it gone (UTC), or null.</summary>
        public DateTime? ResolvedUtc { get; set; }

        /// <summary>When a person last changed the status, assignee or notes (UTC), or null; imports keep the newer.</summary>
        public DateTime? Updated { get; set; }

        /// <summary>Who made that change.</summary>
        public string UpdatedBy { get; set; }

        /// <summary>The key matching this clash across runs: test + the two element keys in a fixed order.</summary>
        [JsonIgnore]
        public string Key => ClashKeys.For(TestId, A, B);

        /// <summary>The point as a vector (internal metres).</summary>
        [JsonIgnore]
        public Vector3 Point => new((float)X, (float)Y, (float)Z);
    }

    /// <summary>
    /// One finished run of a test, for the history (did we clean them up?).
    /// </summary>
    public sealed class ClashRunRecord
    {
        /// <summary>When it finished (UTC).</summary>
        public DateTime Utc { get; set; } = DateTime.UtcNow;

        /// <summary>The test's id.</summary>
        public string TestId { get; set; }

        /// <summary>Who ran it.</summary>
        public string User { get; set; } = Environment.UserName;

        /// <summary>Clashes found by this run.</summary>
        public int Found { get; set; }

        /// <summary>Of those, how many were new.</summary>
        public int New { get; set; }

        /// <summary>How many earlier clashes this run marked resolved.</summary>
        public int NewlyResolved { get; set; }

        /// <summary>The test's clashes per status after the run (<see cref="ClashStatus.All"/> order).</summary>
        public int[] ByStatus { get; set; } = new int[ClashStatus.All.Length];

        /// <summary>Elements in set A and set B.</summary>
        public int ElementsA { get; set; }

        /// <inheritdoc cref="ElementsA"/>
        public int ElementsB { get; set; }

        /// <summary>Seconds taken.</summary>
        public double Seconds { get; set; }

        /// <summary>True when it stopped at the clash limit.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Everything clash detection keeps for a model: search sets, tests, clashes and run history. Stored as
    /// <c>clashes.json</c> in a .bimgo (optional part: older readers ignore it, no format bump) and in a live model's
    /// BimGo folder; the same shape (with the header filled) is the exported report.
    /// </summary>
    public sealed class ClashDocument
    {
        /// <summary>"bimgo-clashes" (stored) or "bimgo-clash-report" (exported).</summary>
        public string Format { get; set; } = ClashFiles.FORMAT;

        /// <summary>The shape's version (1).</summary>
        public int Version { get; set; } = ClashFiles.VERSION;

        /// <summary>Report header: the model's title, or null.</summary>
        public string ModelTitle { get; set; }

        /// <summary>Report header: the model key (<c>ModelProvenance</c>), or null.</summary>
        public string ModelKey { get; set; }

        /// <summary>Report header: who wrote it ("BimGo X.X.X"), or null.</summary>
        public string Generator { get; set; }

        /// <summary>Report header: when it was exported (UTC), or null.</summary>
        public DateTime? ExportedUtc { get; set; }

        /// <summary>The search sets.</summary>
        public List<SearchSet> Sets { get; set; } = new();

        /// <summary>The tests.</summary>
        public List<ClashTest> Tests { get; set; } = new();

        /// <summary>Every clash of every test (resolved ones included).</summary>
        public List<ClashRecord> Clashes { get; set; } = new();

        /// <summary>Run history, oldest first (at most <see cref="ClashDefaults.MAX_RUNS"/>).</summary>
        public List<ClashRunRecord> Runs { get; set; } = new();

        /// <summary>
        /// C-C1: clash groups (<see cref="ClashGroup"/>; null when there are none, so files without groups stay as they
        /// were). Additive: version 1 readers ignore it.
        /// </summary>
        public List<ClashGroup> Groups { get; set; }

        /// <summary>The set with an id, or null (no allocation: the panels call it every frame).</summary>
        public SearchSet FindSet(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            foreach (SearchSet set in Sets)
            {
                if (set.Id == id) { return set; }
            }
            return null;
        }

        /// <summary>The test with an id, or null.</summary>
        public ClashTest FindTest(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            foreach (ClashTest test in Tests)
            {
                if (test.Id == id) { return test; }
            }
            return null;
        }

        /// <summary>
        /// Repairs what a hand-edited or damaged file may hold: null lists, null entries, unknown words, missing ids,
        /// duplicate ids. Returns itself.
        /// </summary>
        public ClashDocument Clean()
        {
            Sets = (Sets ?? new List<SearchSet>()).Where(s => s != null).ToList();
            Tests = (Tests ?? new List<ClashTest>()).Where(t => t != null).ToList();
            Clashes = (Clashes ?? new List<ClashRecord>()).Where(c => c != null && c.A != null && c.B != null && !string.IsNullOrEmpty(c.TestId)).ToList();
            Runs = (Runs ?? new List<ClashRunRecord>()).Where(r => r != null).ToList();

            var setIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (SearchSet set in Sets)
            {
                if (string.IsNullOrWhiteSpace(set.Id) || !setIds.Add(set.Id)) { set.Id = Guid.NewGuid().ToString("N"); setIds.Add(set.Id); }
                set.Name = string.IsNullOrWhiteSpace(set.Name) ? "Set" : set.Name.Trim();
                set.NormaliseRules(); // groups too
            }

            var testIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ClashTest test in Tests)
            {
                if (string.IsNullOrWhiteSpace(test.Id) || !testIds.Add(test.Id)) { test.Id = Guid.NewGuid().ToString("N"); testIds.Add(test.Id); }
                test.Name = string.IsNullOrWhiteSpace(test.Name) ? "Test" : test.Name.Trim();
                test.Kind = ClashKind.Normalise(test.Kind);
                if (double.IsNaN(test.Tolerance) || double.IsInfinity(test.Tolerance)) { test.Tolerance = ClashDefaults.ToleranceFor(test.Kind); }
                test.Tolerance = Math.Clamp(test.Tolerance, 0.0, ClashDefaults.MAX_TOLERANCE);
            }

            var clashIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ClashRecord clash in Clashes)
            {
                if (string.IsNullOrWhiteSpace(clash.Id) || !clashIds.Add(clash.Id)) { clash.Id = Guid.NewGuid().ToString("N"); clashIds.Add(clash.Id); }
                clash.Status = ClashStatus.Normalise(clash.Status);
                clash.A.Link ??= string.Empty;
                clash.B.Link ??= string.Empty;
                clash.A.UniqueId ??= string.Empty;
                clash.B.UniqueId ??= string.Empty;
            }
            foreach (ClashRunRecord run in Runs)
            {
                if (run.ByStatus == null || run.ByStatus.Length != ClashStatus.All.Length)
                {
                    int[] fixedCounts = new int[ClashStatus.All.Length];
                    if (run.ByStatus != null) { Array.Copy(run.ByStatus, fixedCounts, Math.Min(run.ByStatus.Length, fixedCounts.Length)); }
                    run.ByStatus = fixedCounts;
                }
            }
            if (Runs.Count > ClashDefaults.MAX_RUNS) { Runs.RemoveRange(0, Runs.Count - ClashDefaults.MAX_RUNS); }
            ClashGroups.Clean(this); // C-C1: after the clash ids are fixed
            return this;
        }
    }

    /// <summary>
    /// The clash detection defaults agreed for Round C.
    /// </summary>
    public static class ClashDefaults
    {
        /// <summary>Hard tests ignore crossings up to 5 mm.</summary>
        public const double HARD_TOLERANCE = 0.005;

        /// <summary>Clearance tests report anything closer than 50 mm.</summary>
        public const double CLEARANCE_DISTANCE = 0.05;

        /// <summary>The largest tolerance / distance accepted (2 m).</summary>
        public const double MAX_TOLERANCE = 2.0;

        /// <summary>A test stops at this many clashes (with a notice; narrow the sets).</summary>
        public const int MAX_CLASHES_PER_TEST = 10_000;

        /// <summary>Run history kept.</summary>
        public const int MAX_RUNS = 200;

        /// <summary>The default tolerance for a kind.</summary>
        public static double ToleranceFor(string kind) => ClashKind.Normalise(kind) == ClashKind.CLEARANCE ? CLEARANCE_DISTANCE : HARD_TOLERANCE;
    }

    /// <summary>
    /// Clash keys: how a clash is recognised again in a later run.
    /// </summary>
    public static class ClashKeys
    {
        /// <summary>
        /// Test id + both element keys, the smaller first (so A × B and B × A in a self test are the same clash).
        /// </summary>
        public static string For(string testId, ClashElementRef a, ClashElementRef b)
        {
            string ka = a?.Key ?? string.Empty, kb = b?.Key ?? string.Empty;
            return string.CompareOrdinal(ka, kb) <= 0 ? $"{testId}|{ka}|{kb}" : $"{testId}|{kb}|{ka}";
        }
    }
}
