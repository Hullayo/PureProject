using System.Globalization;

namespace PureProject.Core;

public static class RecurrenceCalculator
{
    public static string? NextOccurrence(RecurrenceRule? rule, string? baseDate, DateOnly? today = null)
    {
        if (rule is null) return null;
        var currentDay = today ?? DateOnly.FromDateTime(DateTime.Now);
        if (rule.End == "count" && rule.Count.GetValueOrDefault() <= 0) return null;
        DateOnly? until = DateOnly.TryParseExact(rule.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var limit) ? limit : null;
        if (rule.End == "until" && until < currentDay) return null;
        var date = DateOnly.TryParseExact(baseDate?.Length >= 10 ? baseDate[..10] : baseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : currentDay;
        var interval = Math.Max(1, rule.Interval);
        DateOnly next;
        try
        {
            switch (rule.Freq)
            {
                case "daily": next = date.AddDays(interval); break;
                case "monthly": next = date.AddMonths(interval); break;
                case "yearly": next = date.AddYears(interval); break;
                case "weekly":
                    var weekdays = rule.ByWeekday?.Where(day => day is >= 0 and <= 6).Distinct().Order().ToList() ?? [];
                    if (weekdays.Count == 0) next = date.AddDays(checked(interval * 7));
                    else
                    {
                        var day = (int)date.DayOfWeek;
                        var later = weekdays.Where(d => d > day).ToArray();
                        next = date.AddDays(later.Length > 0 ? later[0] - day : checked(interval * 7) + weekdays[0] - day);
                    }
                    break;
                default: throw new ArgumentException("不支持的循环频率");
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
        if (rule.End == "until" && until is not null && next > until) return null;
        return next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static RecurrenceRule Consume(RecurrenceRule rule)
    {
        var copy = PmSerializer.Copy(rule);
        copy.SourceTaskId = null;
        if (copy.End == "count") copy.Count = Math.Max(0, copy.Count.GetValueOrDefault() - 1);
        return copy;
    }
}
