using ScrapBot.Steam;

namespace ScrapBot.Tests;

public class GraphHistoryTests
{
    [Fact]
    public void Normalize_FiltersGroupsFillsAndTrimsHistory()
    {
        var endDay = new DateTime(2026, 04, 19);
        var loaded = new[]
        {
            new GraphHistoryEntry { Day = endDay.AddDays(-25), Updates = 1 },
            new GraphHistoryEntry { Day = endDay.AddDays(-24), Updates = -4 },
            new GraphHistoryEntry { Day = endDay.AddDays(-23), Updates = 2 },
            new GraphHistoryEntry { Day = endDay.AddDays(-23), Updates = 3 },
            new GraphHistoryEntry { Day = endDay.AddDays(-21), Updates = 5 },
            new GraphHistoryEntry { Day = endDay, Updates = 7 }
        };

        var normalized = GraphHistory.Normalize(loaded, endDay);

        Assert.Equal(26, normalized.Count);
        Assert.Equal(endDay.AddDays(-25), normalized[0].day);
        Assert.Equal(endDay, normalized[^1].day);
        Assert.Equal(5, normalized.Single(x => x.day == endDay.AddDays(-23)).updates);
        Assert.Equal(0, normalized.Single(x => x.day == endDay.AddDays(-22)).updates);
        Assert.Equal(5, normalized.Single(x => x.day == endDay.AddDays(-21)).updates);
        Assert.Equal(7, normalized[^1].updates);
    }

    [Fact]
    public void Normalize_ReturnsEmptyHistoryWhenNoValidEntriesExist()
    {
        var result = GraphHistory.Normalize(
            new[] { new GraphHistoryEntry { Day = new DateTime(2026, 04, 19), Updates = -1 } },
            new DateTime(2026, 04, 19));

        Assert.Empty(result);
    }

    [Fact]
    public void Update_IncrementsExistingDay()
    {
        var day = new DateTime(2026, 04, 19);
        var history = new List<(DateTime day, int updates)>
        {
            (day, 2)
        };

        GraphHistory.Update(history, day, 3);

        Assert.Single(history);
        Assert.Equal((day, 5), history[0]);
    }

    [Fact]
    public void Update_AppendsNewDayAndTrimsToWindow()
    {
        var start = new DateTime(2026, 03, 20);
        var history = Enumerable.Range(0, 30)
            .Select(i => (start.AddDays(i), i))
            .ToList();

        GraphHistory.Update(history, start.AddDays(30), 99);

        Assert.Equal(30, history.Count);
        Assert.Equal((start.AddDays(1), 1), history[0]);
        Assert.Equal((start.AddDays(30), 99), history[^1]);
    }
}
