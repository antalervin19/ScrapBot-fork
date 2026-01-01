// #define STEAM_PACKET_VERBOSE

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamKit2;
using RevoltSharp;
using System.Text.Json;
using System.Text;
using static SteamKit2.SteamApps;

namespace ScrapBot.Steam;

public class Options
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int MaxReconnectDelaySeconds { get; set; }
    public int PICSRefreshDelaySeconds { get; set; }
    public required List<Webhook> Webhooks { get; set; }
}


public class Service : IHostedService
{
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
    private Dictionary<uint, string> steamRating = new() {
        {387990, "0"},
        {588870, "0"}
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
        _steamClient.AddHandler(new VerboseHandler());
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
            var storeTagsFetch = await fetchStoreTags(id);
            if (storeTagsFetch is not null)
            {
                storeTags[id] = storeTagsFetch;
            }
            var rating = await fetchSteamRating(id);
            if (rating is not null)
            {
								steamRating[id] = rating;
            }
        }
    }

    private void OnUserLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        logger.LogInformation("Logged Off");
    }

    private async void OnPICSChanges(SteamApps.PICSChangesCallback callback)
    {
        if (callback.LastChangeNumber == callback.CurrentChangeNumber) return;
        if (callback.CurrentChangeNumber > lastChangeNumber) lastChangeNumber = callback.CurrentChangeNumber;
        var apps = callback.AppChanges.Where(app => Apps.ContainsKey(app.Value.ID)).ToArray();
        if (apps.Length <= 0) return;
        foreach (var (id, app) in apps)
        {
            var tagsCurrent = await fetchStoreTags(id);
						var ratingCurrent = await fetchSteamRating(id);
						if (ratingCurrent is not null) {
							if (steamRating[id] != ratingCurrent) continue;
						}

            if (tagsCurrent is not null)
            {
                if (!storeTags.ContainsKey(id)) continue;

                var tagsOld = storeTags[id];

                var diffKeys = false;
                var sameValues = tagsCurrent.Values.All(tagsOld.Values.Contains);
                if (!sameValues)
                {
                    storeTags[id] = tagsCurrent;
                    continue;
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
                    storeTags[id] = tagsCurrent;
                    continue;
                }
            }
            Apps.TryGetValue(app.ID, out var appName);
            foreach (var webhook in options.Webhooks)
            {
                switch (webhook.type)
                {
                    case "discord":
                        {
                            var content = $"New SteamDB change detected! `{appName} ({app.ID})`  \nhttps://steamdb.info/app/{app.ID}/history/?changeid={app.ChangeNumber}";
                            using StringContent jsonContent = new(
                                JsonSerializer.Serialize(new
                                {
                                    content = content,
                                }),
                                Encoding.UTF8,
                                "application/json"
                            );
                            var res = await httpClient.PostAsync(webhook.token, jsonContent);

                            res.Dispose();
                            break;
                        }
                    case "revolt":
                        {
                            var client = new RevoltClient(webhook.token, ClientMode.Http);
                            await client.StartAsync();

                            var content =
                $"New Steam PICS Change for App `{appName} ({app.ID})`  \nhttps://steamdb.info/app/{app.ID}/history/?changeid={app.ChangeNumber}";

                            if (webhook.revolt_chat == null)
                            {
                                Console.WriteLine("No channel for revolt webhook");
                                return;
                            }

                            var channel = await client.Rest.GetChannelAsync(webhook.revolt_chat);
                            if (channel == null)
                            {
                                Console.WriteLine("Channel for revolt not found");
                                return;
                            }
                            await channel.SendMessageAsync(content);
                            break;
                        }
                }
            }

        }

    }


    private async Task<Dictionary<string, string>?> fetchStoreTags(uint appid)
    {
        var req = await steamApps.PICSGetProductInfo(new PICSRequest(appid), new PICSRequest());
        if (req.Failed || req.Results is null) return null;

        var result = req.Results[0].Apps.Values.ToArray()[0];
        if (result is null) return null;

        return result.KeyValues.GetStoreTagsIfExists();
    }
		private async Task<string?> fetchSteamRating(uint appid) {
        var req = await steamApps.PICSGetProductInfo(new PICSRequest(appid), new PICSRequest());
        if (req.Failed || req.Results is null) return null;
        var result = req.Results[0].Apps.Values.ToArray()[0];
				var reviewPercentage = result.KeyValues.CustomIndex("common/review_percentage")?.Value;

				logger.LogInformation("RATING {}: {}",appid,reviewPercentage);

				return reviewPercentage;

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
