// #define STEAM_PACKET_VERBOSE

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamKit2;
using ScottPlot;
using System.Globalization;
using System.Text.Json;
using static SteamKit2.SteamApps;

namespace ScrapBot.Steam;

public class Options
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int MaxReconnectDelaySeconds { get; set; }
    public int PICSRefreshDelaySeconds { get; set; }
    public required List<WebhookFile> Webhooks { get; set; }
}


public class Service : IHostedService
{
    private static readonly TimeZoneInfo swedishTimeZone = GetSwedishTimeZone();
    private static readonly uint gameAppId = 387990;
    private static readonly uint modToolAppId = 588870;
    private static readonly uint[] graphAppIds = [gameAppId, modToolAppId];
    private static readonly ScottPlot.Color gameGraphColor = new("#ff9800");
    private static readonly ScottPlot.Color modToolGraphColor = new("#4caf50");

    private Dictionary<uint, List<(DateTime day, int updates)>> updateHistoryByApp = new()
    {
        { gameAppId, new() },
        { modToolAppId, new() }
    };
    private static readonly string historyFilePath = "./data/graph_history.json";
    private Dictionary<uint, string> Apps = new() {
        {gameAppId, "Scrap Mechanic"},
        {modToolAppId, "Scrap Mechanic Mod Tool"}
    };


    private readonly ILogger<Service> logger;
    private readonly Options options;

    private readonly SteamClient steamClient;
    private readonly SteamApps steamApps;
    private readonly SteamUser steamUser;
    private readonly SteamFriends steamFriends;
    private readonly CallbackManager callbackManager;

    private readonly Timer timer;
    private readonly Timer midnightTimer;

    private uint lastChangeNumber;

    private bool isAnon;

    private bool isFirstConnection = true;
    private bool isStopping;
    private int reconnectAttempts;

    private Dictionary<uint, Dictionary<string, string>> storeTags = new() {
        {gameAppId, new Dictionary<string,string>()},
        {modToolAppId, new Dictionary<string, string>()}
    };
    private Dictionary<uint, string?> steamRating = new() {
        {gameAppId, null},
        {modToolAppId, null}
    };

    private readonly HttpClient httpClient = new();


    public Service(ILogger<Service> logger, IOptions<Options> options)
    {
        this.logger = logger;
        this.options = options.Value;

        timer = new Timer(TimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        midnightTimer = new Timer(MidnightTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        CheckAnon();

        steamClient = new SteamClient();
        steamApps = steamClient.GetHandler<SteamApps>()!;
        steamUser = steamClient.GetHandler<SteamUser>()!;
        steamFriends = steamClient.GetHandler<SteamFriends>()!;

#if STEAM_PACKET_VERBOSE
        steamClient.AddHandler(new VerboseHandler());
#endif

        callbackManager = new CallbackManager(steamClient);
        callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnClientConnected);
        callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnClientDisconnected);
        callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnUserLoggedOn);
        callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnUserLoggedOff);
        callbackManager.Subscribe<SteamApps.PICSChangesCallback>(OnPICSChanges);
    }

    private void CheckAnon()
    {
        isAnon = options.Username is null || options.Password is null;
    }

    private void TimerCallback(object? _)
    {
        steamApps.PICSGetChangesSince(lastChangeNumber, true, true);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting");

        LoadGraphHistory();
        ScheduleMidnightCallback();

        var callbackTask = new Task(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(5));
            }
        }, cancellationToken, TaskCreationOptions.LongRunning);

        callbackTask.Start();

        logger.LogInformation("Connecting");

        steamClient.Connect();

        logger.LogInformation("Started");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping");

        isStopping = true;
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        midnightTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        steamClient.Disconnect();

        logger.LogInformation("Stopped");

        return Task.CompletedTask;
    }

    private void OnClientConnected(SteamClient.ConnectedCallback callback)
    {
        reconnectAttempts = 0;
        logger.LogInformation("Client {}", isFirstConnection ? "Connected" : "Reconnected");
        isFirstConnection = false;

        CheckAnon();

        logger.LogInformation("Logging On {}", isAnon ? " Anonymously" : string.Empty);

        if (isAnon)
        {
            steamUser.LogOnAnonymous();
        }
        else
        {
            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = options.Username,
                Password = options.Password
            });
        }
    }

    private async void OnClientDisconnected(SteamClient.DisconnectedCallback callback)
    {
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        logger.LogInformation("Disconnected");
        if (isStopping) return;
        var seconds = (int)Math.Min(Math.Pow(2, reconnectAttempts) * 15, options.MaxReconnectDelaySeconds);
        var attempts = " (Attempt " + (reconnectAttempts + 1) + ")";
        logger.LogInformation("Reconnecting in " + seconds + " Second" + (seconds == 1 ? "" : "s") + attempts);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        logger.LogInformation("Reconnecting" + attempts);
        reconnectAttempts += 1;
        steamClient.Connect();
    }

    private async void OnUserLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            logger.LogError($"Log On Failed: EResult.{callback.Result:G}({callback.Result:D})");
            return;
        }

        if (!isAnon) steamFriends.SetPersonaState(EPersonaState.Online);
        logger.LogInformation("Logged On{}", isAnon ? " Anonymously" : string.Empty);
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(options.PICSRefreshDelaySeconds));


        foreach (var (id, _) in Apps)
        {
            var pics = await fetchPICS(id);
            var storeTagsFetch = pics?["common"]["store_tags"].ToDict();
            if (storeTagsFetch is not null)
            {
                storeTags[id] = storeTagsFetch;
            }
            steamRating[id] = pics?["common"]["review_percentage"]?.Value;
        }
    }

    private void OnUserLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        logger.LogInformation("Logged Off");
    }

    private void ScheduleMidnightCallback()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowSwedish = TimeZoneInfo.ConvertTime(nowUtc, swedishTimeZone);
        var nextMidnightSwedish = new DateTimeOffset(
            nowSwedish.Year,
            nowSwedish.Month,
            nowSwedish.Day,
            0, 0, 0,
            nowSwedish.Offset).AddDays(1);
        var nextMidnightUtc = TimeZoneInfo.ConvertTime(nextMidnightSwedish, TimeZoneInfo.Utc);

        midnightTimer.Change(nextMidnightUtc - nowUtc, Timeout.InfiniteTimeSpan);
    }

    private static TimeZoneInfo GetSwedishTimeZone()
    {
        foreach (var timeZoneId in new[] { "Europe/Stockholm", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new TimeZoneNotFoundException("Could not resolve the Swedish time zone.");
    }

    private async void MidnightTimerCallback(object? _)
    {
        if (isStopping) return;

        try
        {
            var today = DateTime.UtcNow.Date;
            if (!graphAppIds.Any(appId => GraphHistory.ShouldSendMidnightGraph(GetHistory(appId), today)))
            {
                foreach (var appId in graphAppIds)
                {
                    UpdateGraphHistory(appId, 0);
                }

                ScheduleMidnightCallback();
                return;
            }

            foreach (var webhook in options.Webhooks)
            {
                Webhook.impl.TryGetValue(webhook.type, out var impl);
                if (impl is null)
                {
                    logger.LogWarning($"Webhook impl not found {impl}");
                    continue;
                }

                var graphPath = GenerateGraph();
                await impl.sendGraph(graphPath, webhook.data);
            }
        } finally
        {
            if (!isStopping)
            {
                ScheduleMidnightCallback();
            }
        }
    }

    internal async Task SendGraphTest(bool useSampleData = false)
    {
        if (useSampleData)
        {
            SeedSampleGraphHistory();
        }
        else
        {
            LoadGraphHistory();
        }

        foreach (var webhook in options.Webhooks)
        {
            Webhook.impl.TryGetValue(webhook.type, out var impl);
            if (impl is null)
            {
                logger.LogWarning($"Webhook impl not found {webhook.type}");
                continue;
            }

            var graphPath = GenerateGraph();
            await impl.sendGraph(graphPath, webhook.data);
        }

        logger.LogInformation("Sent graph test to {Count} webhook(s)", options.Webhooks.Count);
    }

    private void SeedSampleGraphHistory()
    {
        var endDay = DateTime.UtcNow.Date;

        updateHistoryByApp = new Dictionary<uint, List<(DateTime day, int updates)>>
        {
            {
                gameAppId,
                Enumerable.Range(0, 30)
                    .Select(i => (
                        day: endDay.AddDays(-(29 - i)),
                        updates: i % 5 == 0 ? 4 : i % 3))
                    .ToList()
            },
            {
                modToolAppId,
                Enumerable.Range(0, 30)
                    .Select(i => (
                        day: endDay.AddDays(-(29 - i)),
                        updates: i % 4 == 0 ? 1 : (i + 1) % 4))
                    .ToList()
            }
        };
    }

    private async Task<bool> shouldSkipPICSChange(uint appid)
    {
        var pics = await fetchPICS(appid);
        return PICSChangeLogic.ShouldSkipChange(appid, pics, steamRating, storeTags);
    }

    private async void OnPICSChanges(SteamApps.PICSChangesCallback callback)
    {
        if (callback.LastChangeNumber == callback.CurrentChangeNumber) return;
        if (callback.CurrentChangeNumber > lastChangeNumber) lastChangeNumber = callback.CurrentChangeNumber;
        var apps = callback.AppChanges.Where(app => Apps.ContainsKey(app.Value.ID)).ToArray();
        if (apps.Length <= 0) return;
        var changesByApp = new Dictionary<uint, int>();
        foreach (var (id, app) in apps)
        {
            if (await shouldSkipPICSChange(id)) continue;
            changesByApp.TryGetValue(app.ID, out var changeCount);
            changesByApp[app.ID] = changeCount + 1;
            Apps.TryGetValue(app.ID, out var appName);
            var content = $"New SteamDB change detected! `{appName} ({app.ID})`  \nhttps://steamdb.info/app/{app.ID}/history/?changeid={app.ChangeNumber}";
            foreach (var webhook in options.Webhooks)
            {
                Webhook.impl.TryGetValue(webhook.type, out var impl);
                if (impl is null)
                {
                    logger.LogWarning($"Webhook impl not found: {webhook.type}");
                    continue;
                }
                await impl.send(content, webhook.data);
            }
        }

        foreach (var (appId, changeCount) in changesByApp)
        {
            UpdateGraphHistory(appId, changeCount);
        }
    }

    private List<(DateTime day, int updates)> GetHistory(uint appId)
    {
        if (!updateHistoryByApp.TryGetValue(appId, out var history))
        {
            history = new List<(DateTime day, int updates)>();
            updateHistoryByApp[appId] = history;
        }

        return history;
    }

    private void UpdateGraphHistory(uint appId, int changeCount)
    {
        GraphHistory.Update(GetHistory(appId), DateTime.UtcNow.Date, changeCount);
        SaveGraphHistory();
    }

    private void SaveGraphHistory()
    {
        try
        {
            var serializable = updateHistoryByApp
                .SelectMany(app => app.Value.Select(x => new GraphHistoryEntry
                {
                    AppId = app.Key,
                    Day = x.day,
                    Updates = x.updates
                }))
                .ToList();

            var json = JsonSerializer.Serialize(serializable, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(historyFilePath, json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save graph history to {Path}", historyFilePath);
        }
    }

    private void LoadGraphHistory()
    {
        try
        {
            if (!File.Exists(historyFilePath))
                return;

            var json = File.ReadAllText(historyFilePath);
            var loaded = JsonSerializer.Deserialize<List<GraphHistoryEntry>>(json);
            if (loaded is null)
                return;

            updateHistoryByApp = GraphHistory.NormalizeByApp(loaded, DateTime.UtcNow.Date);

            foreach (var appId in graphAppIds)
            {
                GetHistory(appId);
            }

            logger.LogInformation("Loaded {Count} graph history entries from {Path}", loaded.Count, historyFilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load graph history from {Path}", historyFilePath);
        }
    }

    private string GenerateGraph()
    {
        int window = 30;
        var endDay = DateTime.UtcNow.Date;
        var days = Enumerable.Range(0, window)
            .Select(i => endDay.AddDays(-(window - 1 - i)))
            .ToArray();
        double[] xs = Enumerable.Range(0, window).Select(i => (double)i).ToArray();
        string[] labels = days.Select(d =>
            d == DateTime.UtcNow.Date ? "Today" :
            d.ToString("dd/MM")
        ).ToArray();

        var plt = new Plot();
        var fg = new ScottPlot.Color("#d4d4d4");
        var bg = new ScottPlot.Color("#0a0a0a");

        plt.Font.Set("Geist");
        plt.Grid.MajorLineColor = new ScottPlot.Color("#300530");

        var appSeries = new[]
        {
            (AppId: modToolAppId, Name: "Scrap Mechanic Mod Tool", Color: modToolGraphColor),
            (AppId: gameAppId, Name: "Scrap Mechanic", Color: gameGraphColor)
        };

        var allValues = new List<double>();

        foreach (var series in appSeries)
        {
            var historyLookup = GetHistory(series.AppId).ToDictionary(x => x.day, x => x.updates);
            double[] ys = days.Select(day => historyLookup.TryGetValue(day, out var updates) ? (double)updates : 0).ToArray();
            allValues.AddRange(ys);
            var line = plt.Add.Scatter(xs, ys);
            line.Color = series.Color;
            line.LineWidth = 2;
            line.MarkerSize = 6;
            line.MarkerShape = MarkerShape.FilledCircle;
            line.MarkerFillColor = series.Color;
        }

        plt.Legend.ManualItems.Clear();
        plt.Legend.ManualItems.Add(new LegendItem
        {
            LabelText = "Scrap Mechanic",
            LineColor = gameGraphColor,
            MarkerColor = gameGraphColor,
            MarkerFillColor = gameGraphColor,
            MarkerLineColor = gameGraphColor,
            MarkerShape = MarkerShape.FilledCircle,
            LineWidth = 2
        });
        plt.Legend.ManualItems.Add(new LegendItem
        {
            LabelText = "Scrap Mechanic Mod Tool",
            LineColor = modToolGraphColor,
            MarkerColor = modToolGraphColor,
            MarkerFillColor = modToolGraphColor,
            MarkerLineColor = modToolGraphColor,
            MarkerShape = MarkerShape.FilledCircle,
            LineWidth = 2
        });

        plt.ShowLegend(ScottPlot.Alignment.UpperLeft);

        plt.Axes.Left.IsVisible = true;
        plt.Axes.Left.TickLabelStyle.ForeColor = fg;
        plt.YLabel("Updates");
        plt.Axes.Left.Label.ForeColor = fg;
        plt.Axes.Top.IsVisible = false;
        plt.Axes.Right.IsVisible = false;
        plt.Axes.Bottom.TickLabelStyle.ForeColor = fg;
        plt.Axes.Color(fg);

        double yMinData = allValues.Min();
        double yMaxData = allValues.Max();
        (double yAxisMin, double yAxisMax, double yTickStep) = GraphScale.GetYAxisScale(yMinData, yMaxData);
        plt.Axes.Left.Min = yAxisMin;
        plt.Axes.Left.Max = yAxisMax;

        int tickCount = (int)Math.Round((yAxisMax - yAxisMin) / yTickStep) + 1;
        var yTicks = Enumerable.Range(0, tickCount)
            .Select(i => yAxisMin + (i * yTickStep))
            .ToArray();
        plt.Axes.Left.SetTicks(yTicks, yTicks.Select(t => ((int)t).ToString()).ToArray());

        for (int i = 0; i < labels.Length; i++)
        {
            bool isFirst = i == 0;
            bool isLast = labels[i] == "Today";
            bool isNth = i % 5 == 0;
            if (!(isFirst || isLast || isNth))
                labels[i] = "";
        }

        plt.XLabel("Date");
        plt.Axes.Bottom.Label.ForeColor = fg;
        plt.Axes.Bottom.SetTicks(xs, labels);

        plt.FigureBackground.Color = bg;
        plt.DataBackground.Color = bg;
        plt.DataBorder.Color = fg;

        var filePath = $"./data/steam_graph.png";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        plt.SavePng(filePath, 900, 400);

        return filePath;
    }

    private async Task<KeyValue?> fetchPICS(uint appid)
    {
        var req = await steamApps.PICSGetProductInfo(new PICSRequest(appid), new PICSRequest());
        if (req.Failed || req.Results is null) return null;
        return req.Results[0].Apps.Values.ToArray()[0].KeyValues;
    }
}

#if STEAM_PACKET_VERBOSE
public sealed partial class VerboseHandler : ClientMsgHandler {
    public override void HandleMsg(IPacketMsg packetMsg) {
        if(packetMsg.MsgType == EMsg.Multi) return;
        Console.WriteLine(packetMsg.MsgType);
    }
}
#endif
