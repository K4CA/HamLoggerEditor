using System.Data;
using System.Globalization;

namespace HamLoggerEditor.Rules;

/// <summary>How a condition's value compares to a cell.</summary>
public enum RuleOperator { Equals, NotEquals, GreaterThan, LessThan }

/// <summary>How the first and (optional) second condition combine.</summary>
public enum RuleCombinator { And, Or }

/// <summary>
/// The data type a grid column is treated as, inferred from its current contents. Drives both how
/// comparisons are evaluated (numeric/date/time by value, everything else as text) and which kind
/// of value editor the rule dialog shows for that column.
/// </summary>
public enum ValueKind { Text, Integer, Decimal, Date, Time }

/// <summary>
/// IF (ColumnA OperatorA ValueA) [AND/OR (ColumnB OperatorB ValueB)] THEN (ColumnC = ValueC).
/// Column names and values are already in the grid's on-screen form (ADIF-formatted where relevant,
/// e.g. dates as YYYYMMDD); see <see cref="ConditionalRuleEngine"/> for how they are compared/applied.
/// </summary>
public sealed record ConditionalRule(
    string ColumnA, RuleOperator OperatorA, string ValueA,
    bool HasSecondCondition, RuleCombinator Combinator, string? ColumnB, RuleOperator? OperatorB, string? ValueB,
    string ColumnC, string ValueC);

/// <summary>Infers column data types and evaluates/applies a <see cref="ConditionalRule"/> against a DataTable.</summary>
public static class ConditionalRuleEngine
{
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

    /// <summary>Counts rows the rule's condition matches, without changing anything.</summary>
    public static int CountMatches(DataTable table, ConditionalRule rule)
    {
        ValueKind kindA = InferValueKind(table, rule.ColumnA);
        ValueKind? kindB = rule.ColumnB is null ? null : InferValueKind(table, rule.ColumnB);
        int count = 0;
        foreach (DataRow row in table.Rows)
            if (Matches(row, rule, kindA, kindB)) count++;
        return count;
    }

    /// <summary>
    /// Sets ColumnC = ValueC on every row the condition matches. Returns how many rows matched and how
    /// many actually changed (a matched row already holding ValueC counts toward Matched but not Updated).
    /// </summary>
    public static (int Matched, int Updated) Apply(DataTable table, ConditionalRule rule)
    {
        ValueKind kindA = InferValueKind(table, rule.ColumnA);
        ValueKind? kindB = rule.ColumnB is null ? null : InferValueKind(table, rule.ColumnB);
        int matched = 0, updated = 0;
        foreach (DataRow row in table.Rows)
        {
            if (!Matches(row, rule, kindA, kindB)) continue;
            matched++;
            string current = row[rule.ColumnC] as string ?? string.Empty;
            if (!string.Equals(current, rule.ValueC, StringComparison.Ordinal))
            {
                row[rule.ColumnC] = rule.ValueC;
                updated++;
            }
        }
        return (matched, updated);
    }

    private static bool Matches(DataRow row, ConditionalRule rule, ValueKind kindA, ValueKind? kindB)
    {
        bool condA = Evaluate(row, rule.ColumnA, rule.OperatorA, rule.ValueA, kindA);
        if (!rule.HasSecondCondition || rule.ColumnB is null || rule.OperatorB is null || rule.ValueB is null || kindB is null)
            return condA;
        bool condB = Evaluate(row, rule.ColumnB, rule.OperatorB.Value, rule.ValueB, kindB.Value);
        return rule.Combinator == RuleCombinator.And ? condA && condB : condA || condB;
    }

    private static bool Evaluate(DataRow row, string column, RuleOperator op, string compareValue, ValueKind kind)
    {
        string cell = (row[column] as string ?? string.Empty).Trim();
        return op switch
        {
            RuleOperator.Equals => ValuesEqual(cell, compareValue, kind),
            RuleOperator.NotEquals => !ValuesEqual(cell, compareValue, kind),
            RuleOperator.GreaterThan => CompareValues(cell, compareValue, kind) > 0,
            RuleOperator.LessThan => CompareValues(cell, compareValue, kind) < 0,
            _ => false
        };
    }

    private static bool ValuesEqual(string a, string b, ValueKind kind) => kind switch
    {
        ValueKind.Integer => long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out long la)
            && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lb) && la == lb,
        ValueKind.Decimal => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && Math.Abs(da - db) < 1e-9,
        ValueKind.Date => NormalizeDigits(a, 8) == NormalizeDigits(b, 8),
        ValueKind.Time => NormalizeDigits(a, 6) == NormalizeDigits(b, 6),
        _ => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
    };

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

    /// <summary>Keeps only ASCII digits and pads/truncates to a fixed width (mirrors MainForm's date/time sort key).</summary>
    private static string NormalizeDigits(string value, int width)
    {
        string digits = new(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= width ? digits[..width] : digits.PadRight(width, '0');
    }
}
