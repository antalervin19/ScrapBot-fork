namespace ScrapBot.Steam;

internal sealed class GraphHistoryEntry
{
    public DateTime Day { get; set; }
    public int Updates { get; set; }
}

internal static class GraphHistory
{
    internal static List<(DateTime day, int updates)> Normalize(
        IEnumerable<GraphHistoryEntry> loaded,
        DateTime endDay,
        int maxDays = 30)
    {
        var history = loaded
            .Where(x => x.Updates >= 0)
            .GroupBy(x => x.Day.Date)
            .Select(g => (day: g.Key, updates: g.Sum(e => e.Updates)))
            .OrderBy(x => x.day)
            .ToList();

        if (history.Count == 0)
        {
            return history;
        }

        var normalized = new List<(DateTime day, int updates)>();
        for (var day = history[0].day; day <= endDay.Date; day = day.AddDays(1))
        {
            var entry = history.FirstOrDefault(x => x.day == day);
            normalized.Add((day, entry == default ? 0 : entry.updates));
        }

        return normalized.TakeLast(maxDays).ToList();
    }

    internal static void Update(
        List<(DateTime day, int updates)> history,
        DateTime today,
        int changeCount,
        int maxDays = 30)
    {
        today = today.Date;

        if (history.Count > 0 && history[^1].day == today)
        {
            history[^1] = (today, history[^1].updates + changeCount);
        }
        else
        {
            history.Add((today, changeCount));
        }

        if (history.Count > maxDays)
        {
            history.RemoveAt(0);
        }
    }
}

internal static class GraphScale
{
    internal static (double Min, double Max, double Step) GetYAxisScale(double yMin, double yMax)
    {
        if (yMin == yMax)
        {
            double padding = Math.Max(1, yMax * 0.2);
            yMin = Math.Max(0, yMin - padding);
            yMax += padding;
        }

        double range = Math.Max(yMax - yMin, 1);
        double paddingSize = Math.Max(range * 0.1, 0.5);
        double paddedMin = Math.Max(0, yMin - paddingSize);
        double paddedMax = yMax + paddingSize;

        double step = GetNiceTickStep(paddedMax - paddedMin);
        double axisMin = Math.Floor(paddedMin / step) * step;
        double axisMax = Math.Ceiling(paddedMax / step) * step;

        if (axisMin == axisMax)
        {
            axisMax = axisMin + step;
        }

        return (axisMin, axisMax, step);
    }

    internal static double GetNiceTickStep(double range)
    {
        const int targetTickCount = 6;
        double roughStep = range / targetTickCount;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(roughStep, 1))));
        double normalizedStep = roughStep / magnitude;

        if (normalizedStep <= 1)
            return magnitude;
        if (normalizedStep <= 2)
            return 2 * magnitude;
        if (normalizedStep <= 5)
            return 5 * magnitude;

        return 10 * magnitude;
    }
}
