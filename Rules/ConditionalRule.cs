using System.Data;
using System.Globalization;

namespace HamLoggerEditor.Rules;

/// <summary>How a condition compares a cell to its value.</summary>
public enum RuleOperator
{
    Equals, NotEquals,
    GreaterThan, GreaterOrEqual, LessThan, LessOrEqual,
    Contains, NotContains, StartsWith, EndsWith,
    IsBlank, IsNotBlank
}

/// <summary>How a condition joins the conditions before it.</summary>
public enum RuleCombinator { And, Or }

/// <summary>
/// The data type a grid column is treated as, inferred from its current contents. Drives how
/// comparisons are evaluated (numeric/date/time by value, everything else as text) and how the
/// rule dialog checks and normalizes the values typed for that column.
/// </summary>
public enum ValueKind { Text, Integer, Decimal, Date, Time }

/// <summary>One test: Column Operator Value. <see cref="Combinator"/> is ignored on the first condition.</summary>
public sealed record RuleCondition(RuleCombinator Combinator, string Column, RuleOperator Operator, string Value);

/// <summary>One result: set Column to Value (an empty Value clears the field).</summary>
public sealed record RuleAction(string Column, string Value);

/// <summary>
/// IF condition [AND/OR condition ...] THEN set field = value [, field = value ...].
/// AND binds tighter than OR, as in most query languages: "A OR B AND C" means "A OR (B AND C)".
/// Column names and values are in the grid's on-screen form (ADIF-formatted where relevant,
/// e.g. dates as YYYYMMDD); see <see cref="ConditionalRuleEngine"/> for how they are compared/applied.
/// </summary>
public sealed record ConditionalRule(IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions)
{
    /// <summary>Human-readable list of the THEN assignments, e.g. MODE = "MFSK", SUBMODE = "FT4".</summary>
    public string DescribeActions() =>
        string.Join(", ", Actions.Select(a => a.Value.Length == 0 ? $"{a.Column} = (blank)" : $"{a.Column} = \"{a.Value}\""));
}

/// <summary>Infers column data types and evaluates/applies a <see cref="ConditionalRule"/> against a DataTable.</summary>
public static class ConditionalRuleEngine
{
    /// <summary>True for operators that look at the cell only and take no value.</summary>
    public static bool NeedsValue(RuleOperator op) => op is not (RuleOperator.IsBlank or RuleOperator.IsNotBlank);

    /// <summary>True for operators that always compare as text, whatever the column's type.</summary>
    public static bool IsTextOperator(RuleOperator op) =>
        op is RuleOperator.Contains or RuleOperator.NotContains or RuleOperator.StartsWith or RuleOperator.EndsWith;

    /// <summary>
    /// Infers a column's data type from its name (DATE/TIME fields) or, failing that, by sampling its
    /// current values: a column is Integer/Decimal only when every non-empty value parses as one.
    /// </summary>
    public static ValueKind InferValueKind(DataTable table, string columnName)
    {
        if (!table.Columns.Contains(columnName)) return ValueKind.Text;
        if (columnName.Contains("DATE", StringComparison.OrdinalIgnoreCase)) return ValueKind.Date;
        if (columnName.Contains("TIME", StringComparison.OrdinalIgnoreCase)) return ValueKind.Time;

        bool anyValue = false, allInt = true, allDecimal = true;
        foreach (DataRow row in table.Rows)
        {
            if (row[columnName] is not string raw) continue;
            string s = raw.Trim();
            if (s.Length == 0) continue;
            anyValue = true;
            if (allInt && !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) allInt = false;
            if (allDecimal && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) allDecimal = false;
            if (!allInt && !allDecimal) break;
        }
        if (!anyValue) return ValueKind.Text;
        if (allInt) return ValueKind.Integer;
        if (allDecimal) return ValueKind.Decimal;
        return ValueKind.Text;
    }

    /// <summary>
    /// Checks a typed value against the column's type and returns it in ADIF form (dates YYYYMMDD,
    /// times HHMMSS or HHMM as typed, numbers invariant). Returns false with a reason if it does not fit.
    /// Empty input is accepted as-is (it means "blank").
    /// </summary>
    public static bool TryNormalizeValue(string input, ValueKind kind, out string value, out string error)
    {
        string s = input.Trim();
        value = s;
        error = string.Empty;
        if (s.Length == 0) return true;
        switch (kind)
        {
            case ValueKind.Integer:
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                { value = l.ToString(CultureInfo.InvariantCulture); return true; }
                error = "a whole number"; return false;
            case ValueKind.Decimal:
                if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                { value = d.ToString(CultureInfo.InvariantCulture); return true; }
                error = "a number such as 14.074"; return false;
            case ValueKind.Date:
            {
                string digits = Digits(s);
                if (digits.Length == 8 && DateTime.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                { value = digits; return true; }
                error = "a date as YYYYMMDD or YYYY-MM-DD"; return false;
            }
            case ValueKind.Time:
            {
                string digits = Digits(s);
                bool ok = digits.Length is 4 or 6
                    && int.Parse(digits[..2], CultureInfo.InvariantCulture) < 24
                    && int.Parse(digits[2..4], CultureInfo.InvariantCulture) < 60
                    && (digits.Length == 4 || int.Parse(digits[4..6], CultureInfo.InvariantCulture) < 60);
                if (ok) { value = digits; return true; }
                error = "a UTC time as HHMM or HHMMSS"; return false;
            }
            default:
                return true;
        }
    }

    /// <summary>Counts rows the rule's conditions match, without changing anything.</summary>
    public static int CountMatches(DataTable table, ConditionalRule rule)
    {
        var kinds = ConditionKinds(table, rule);
        int count = 0;
        foreach (DataRow row in table.Rows)
            if (Matches(row, rule, kinds)) count++;
        return count;
    }

    /// <summary>
    /// Applies every THEN assignment to every matching row. Returns how many rows matched and how many
    /// actually changed (a matched row already holding all the values counts toward Matched only).
    /// </summary>
    public static (int Matched, int Updated) Apply(DataTable table, ConditionalRule rule)
    {
        var kinds = ConditionKinds(table, rule);
        int matched = 0, updated = 0;
        foreach (DataRow row in table.Rows)
        {
            if (!Matches(row, rule, kinds)) continue;
            matched++;
            bool changed = false;
            foreach (RuleAction action in rule.Actions)
            {
                string current = row[action.Column] as string ?? string.Empty;
                if (string.Equals(current, action.Value, StringComparison.Ordinal)) continue;
                row[action.Column] = action.Value.Length == 0 ? DBNull.Value : action.Value;
                changed = true;
            }
            if (changed) updated++;
        }
        return (matched, updated);
    }

    private static ValueKind[] ConditionKinds(DataTable table, ConditionalRule rule) =>
        rule.Conditions.Select(c => InferValueKind(table, c.Column)).ToArray();

    /// <summary>OR of AND-groups: a new group starts at every condition joined with OR.</summary>
    private static bool Matches(DataRow row, ConditionalRule rule, ValueKind[] kinds)
    {
        if (rule.Conditions.Count == 0) return false;
        bool group = true;
        for (int i = 0; i < rule.Conditions.Count; i++)
        {
            RuleCondition c = rule.Conditions[i];
            if (i > 0 && c.Combinator == RuleCombinator.Or)
            {
                if (group) return true;
                group = true;
            }
            if (group) group = Evaluate(row, c, kinds[i]);
        }
        return group;
    }

    private static bool Evaluate(DataRow row, RuleCondition c, ValueKind kind)
    {
        string cell = (row[c.Column] as string ?? string.Empty).Trim();
        string v = c.Value;
        return c.Operator switch
        {
            RuleOperator.Equals => ValuesEqual(cell, v, kind),
            RuleOperator.NotEquals => !ValuesEqual(cell, v, kind),
            RuleOperator.GreaterThan => CompareValues(cell, v, kind) > 0,
            RuleOperator.GreaterOrEqual => CompareValues(cell, v, kind) >= 0,
            RuleOperator.LessThan => CompareValues(cell, v, kind) < 0,
            RuleOperator.LessOrEqual => CompareValues(cell, v, kind) <= 0,
            RuleOperator.Contains => cell.Contains(v, StringComparison.OrdinalIgnoreCase),
            RuleOperator.NotContains => !cell.Contains(v, StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => cell.StartsWith(v, StringComparison.OrdinalIgnoreCase),
            RuleOperator.EndsWith => cell.EndsWith(v, StringComparison.OrdinalIgnoreCase),
            RuleOperator.IsBlank => cell.Length == 0,
            RuleOperator.IsNotBlank => cell.Length > 0,
            _ => false
        };
    }

    private static bool ValuesEqual(string a, string b, ValueKind kind)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length;
        return kind switch
        {
            ValueKind.Integer => long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out long la)
                && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lb) && la == lb,
            ValueKind.Decimal => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && Math.Abs(da - db) < 1e-9,
            ValueKind.Date => NormalizeDigits(a, 8) == NormalizeDigits(b, 8),
            ValueKind.Time => NormalizeDigits(a, 6) == NormalizeDigits(b, 6),
            _ => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static int CompareValues(string a, string b, ValueKind kind) => kind switch
    {
        ValueKind.Integer => (long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out long la) ? la : 0L)
            .CompareTo(long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lb) ? lb : 0L),
        ValueKind.Decimal => (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da) ? da : 0d)
            .CompareTo(double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) ? db : 0d),
        ValueKind.Date => string.CompareOrdinal(NormalizeDigits(a, 8), NormalizeDigits(b, 8)),
        ValueKind.Time => string.CompareOrdinal(NormalizeDigits(a, 6), NormalizeDigits(b, 6)),
        _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase)
    };

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    /// <summary>Keeps only ASCII digits and pads/truncates to a fixed width (mirrors MainForm's date/time sort key).</summary>
    private static string NormalizeDigits(string value, int width)
    {
        string digits = Digits(value);
        return digits.Length >= width ? digits[..width] : digits.PadRight(width, '0');
    }
}
