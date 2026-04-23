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

    private List<(DateTime day, int updates)> updateHistory = new();
    private static readonly string historyFilePath = "./data/graph_history.json";
    private Dictionary<uint, string> Apps = new() {
        {387990, "Scrap Mechanic"},
        {588870, "Scrap Mechanic Mod Tool"}
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
        {387990, new Dictionary<string,string>()},
        {588870, new Dictionary<string, string>()}
    };
    private Dictionary<uint, string?> steamRating = new() {
        {387990, null},
        {588870, null}
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
            if (!GraphHistory.ShouldSendMidnightGraph(updateHistory, today))
            {
                UpdateGraphHistory(0);
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
        var smChanges = 0;
        foreach (var (id, app) in apps)
        {
            if (await shouldSkipPICSChange(id)) continue;
            if (app.ID == 387990) smChanges++;
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
        UpdateGraphHistory(smChanges);
    }

    private void UpdateGraphHistory(int changeCount)
    {
        GraphHistory.Update(updateHistory, DateTime.UtcNow.Date, changeCount);
        SaveGraphHistory();
    }

    private void SaveGraphHistory()
    {
        try
        {
            var serializable = updateHistory.Select(x => new GraphHistoryEntry
            {
                Day = x.day,
                Updates = x.updates
            }).ToList();

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

            updateHistory = GraphHistory.Normalize(loaded, DateTime.UtcNow.Date);

            logger.LogInformation("Loaded {Count} graph history entries from {Path}", updateHistory.Count, historyFilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load graph history from {Path}", historyFilePath);
        }
    }

    private string GenerateGraph()
    {
        int window = 30;
        var days = updateHistory.Select(x => x.day).ToArray();
        var updates = updateHistory.Select(x => x.updates).ToArray();

        if (days.Length < window)
        {
            var missing = window - days.Length;
            var start = days.Length > 0 ? days[0].AddDays(-missing) : DateTime.UtcNow.Date.AddDays(-(window - 1));
            var fillDays = Enumerable.Range(0, missing).Select(i => start.AddDays(i)).ToArray();
            days = fillDays.Concat(days).ToArray();
            updates = Enumerable.Repeat(0, missing).Concat(updates).ToArray();
        }

        double[] xs = Enumerable.Range(0, window).Select(i => (double)i).ToArray();
        double[] ys = updates.Select(x => (double)x).ToArray();
        string[] labels = days.Select(d =>
            d == DateTime.UtcNow.Date ? "Today" :
            d.ToString("MM-dd")
        ).ToArray();

        var plt = new Plot();
        var fg = new ScottPlot.Color("#d4d4d4");
        var bg = new ScottPlot.Color("#0a0a0a");
        var line = plt.Add.Scatter(xs, ys);

        plt.Font.Set("Geist");
        line.Color = fg;
        line.LineWidth = 2;
        line.MarkerSize = 6;
        line.MarkerShape = MarkerShape.FilledCircle;
        line.MarkerFillColor = fg;
        plt.Grid.MajorLineColor = new ScottPlot.Color("#300530");

        plt.Axes.Left.IsVisible = true;
        plt.Axes.Left.TickLabelStyle.ForeColor = fg;
        plt.YLabel("Updates");
        plt.Axes.Left.Label.ForeColor = fg;
        plt.Axes.Top.IsVisible = false;
        plt.Axes.Right.IsVisible = false;
        plt.Axes.Bottom.TickLabelStyle.ForeColor = fg;
        plt.Axes.Color(fg);

        double yMinData = ys.Min();
        double yMaxData = ys.Max();
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
