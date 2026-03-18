// #define STEAM_PACKET_VERBOSE

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamKit2;
using ScottPlot;
using System.Globalization;
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
    private List<(DateTime day, int updates)> updateHistory = new();
    private string lastGraphPath = string.Empty;
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

        logger.LogInformation("Logging On{}", isAnon ? " Anonymously" : string.Empty);

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

    private async Task<bool> shouldSkipPICSChange(uint appid)
    {
        var pics = await fetchPICS(appid);
        if (pics is null) return false;
        var ratingCurrent = pics["common"]["review_percentage"]?.Value;
        var tagsCurrent = pics["common"]["store_tags"].ToDict();
        if (ratingCurrent is not null && steamRating[appid] != ratingCurrent)
        {
            steamRating[appid] = ratingCurrent;
            return true;
        }

        if (tagsCurrent is not null)
        {
            if (!storeTags.ContainsKey(appid)) return false;

            var tagsOld = storeTags[appid];

            var diffKeys = false;
            var sameValues = tagsCurrent.Values.All(tagsOld.Values.Contains);
            if (!sameValues)
            {
                storeTags[appid] = tagsCurrent;
                return true;
            }

            foreach (var (k, v) in tagsCurrent)
            {
                tagsOld.TryGetValue(k, out var v2);
                if (v != v2)
                {
                    diffKeys = true;
                    break;
                }
            }
            if (tagsCurrent.Count != tagsOld.Count || diffKeys)
            {
                storeTags[appid] = tagsCurrent;
                return true;
            }

        }
        return false;
    }

    private async void OnPICSChanges(SteamApps.PICSChangesCallback callback)
    {
        if (callback.LastChangeNumber == callback.CurrentChangeNumber) return;
        if (callback.CurrentChangeNumber > lastChangeNumber) lastChangeNumber = callback.CurrentChangeNumber;
        var apps = callback.AppChanges.Where(app => Apps.ContainsKey(app.Value.ID)).ToArray();
        if (apps.Length <= 0) return;
        foreach (var (id, app) in apps)
        {
            if (await shouldSkipPICSChange(id)) continue;
            Apps.TryGetValue(app.ID, out var appName);
            var content = $"New SteamDB change detected! `{appName} ({app.ID})`  \nhttps://steamdb.info/app/{app.ID}/history/?changeid={app.ChangeNumber}";
            foreach (var webhook in options.Webhooks)
            {
                Webhook.impl.TryGetValue(webhook.type, out var impl);
                if (impl is null)
                {
                    logger.LogWarning("Webhook impl not found");
                    continue;
                }
                await impl.send(content, webhook.data);
            }
        }

        UpdateGraphHistory();
        var graphPath = GenerateGraph();
        DeletePreviousGraphIfSameDay(graphPath);
        foreach (var webhook in options.Webhooks)
        {
            Webhook.impl.TryGetValue(webhook.type, out var impl);
            if (impl is null) continue;
            await impl.send($"7-Day Update Graph: {graphPath}", webhook.data);
        }
        lastGraphPath = graphPath;
    }
    private void UpdateGraphHistory()
    {
        var today = DateTime.UtcNow.Date;
        if (updateHistory.Count > 0 && updateHistory[^1].day == today)
        {
            updateHistory[^1] = (today, updateHistory[^1].updates + 1);
        }
        else
        {
            updateHistory.Add((today, 1));
        }
        if (updateHistory.Count > 7)
            updateHistory.RemoveAt(0);
    }

    private string GenerateGraph()
    {
        var days = updateHistory.Select(x => x.day).ToArray();
        var updates = updateHistory.Select(x => x.updates).ToArray();

        if (days.Length < 7)
        {
            var missing = 7 - days.Length;
            var start = days.Length > 0 ? days[0].AddDays(-missing) : DateTime.UtcNow.Date.AddDays(-6);
            var fillDays = Enumerable.Range(0, missing).Select(i => start.AddDays(i)).ToArray();

            days = fillDays.Concat(days).ToArray();
            updates = Enumerable.Repeat(0, missing).Concat(updates).ToArray();
        }

        double[] xs = Enumerable.Range(0, 7).Select(i => (double)i).ToArray();
        double[] ys = updates.Select(x => (double)x).ToArray();
        string[] labels = days.Select(d => d == DateTime.UtcNow.Date ? "Today" : d == DateTime.UtcNow.Date.AddDays(-1) ? "Yesterday" : d.Day.ToString()).ToArray();

        var plt = new Plot();
        var green = new ScottPlot.Color(0, 255, 120);
        var line = plt.Add.Scatter(xs, ys);

        line.Color = green;
        line.LineWidth = 2;
        line.MarkerSize = 6;
        line.MarkerShape = MarkerShape.FilledCircle;
        line.MarkerFillColor = green;
        plt.FigureBackground.Color = ScottPlot.Colors.Transparent;
        plt.DataBackground.Color = ScottPlot.Colors.Transparent;
        plt.Grid.MajorLineColor = new ScottPlot.Color(0, 255, 120, 30);
        plt.Axes.Left.IsVisible = false;
        plt.Axes.Top.IsVisible = false;
        plt.Axes.Right.IsVisible = false;
        plt.Axes.Bottom.TickLabelStyle.ForeColor = green;
        plt.Axes.Bottom.SetTicks(xs, labels);

        plt.XLabel("Date");

        Array.Reverse(xs);
        Array.Reverse(ys);
        Array.Reverse(labels);

        plt.Axes.Bottom.SetTicks(xs, labels);

        var fileName = $"steam_graph_{DateTime.UtcNow:yyyyMMdd}.png";
        var filePath = Path.Combine(AppContext.BaseDirectory, fileName);
        plt.SavePng(filePath, 600, 300);

        return filePath;
    }

    private void DeletePreviousGraphIfSameDay(string currentPath)
    {
        if (!string.IsNullOrEmpty(lastGraphPath) && lastGraphPath != currentPath)
        {
            var lastDate = Path.GetFileNameWithoutExtension(lastGraphPath)?.Split('_').LastOrDefault();
            var today = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (lastDate == today && File.Exists(lastGraphPath))
            {
                File.Delete(lastGraphPath);
            }
        }
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
