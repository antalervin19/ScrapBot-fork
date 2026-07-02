using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using ScottPlot;

namespace ScrapBot;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        Fonts.AddFontFile("Geist", "./Geist.ttf");

        bool sendGraphTest = args.Any(x => x.Equals("--send-graph-test", StringComparison.OrdinalIgnoreCase));
        bool useSampleData = args.Any(x => x.Equals("--sample-data", StringComparison.OrdinalIgnoreCase));
        string? testWebhookUrl = GetArgument(args, "--webhook-url");

        List<WebhookFile>? webhooks = null;
        if (!sendGraphTest || string.IsNullOrWhiteSpace(testWebhookUrl))
        {
            if (!File.Exists("./webhooks.json"))
            {
                Console.WriteLine("Couldn't find webhooks.json. Use --webhook-url for --send-graph-test, or add webhooks.json for normal mode.");
                return;
            }

            var webhookFile = await File.ReadAllTextAsync("./webhooks.json");
            webhooks = JsonSerializer.Deserialize<List<WebhookFile>>(webhookFile);

            if (webhooks == null)
            {
                Console.WriteLine("Couldn't parse webhooks.json");
                return;
            }
        }

        var host = Host.CreateDefaultBuilder(args)
                       .ConfigureServices((ctx, services) =>
                       {
                           services.AddOptions<Steam.Options>()
                                   .Configure(options =>
                                   {
                                       options.MaxReconnectDelaySeconds = (int)TimeSpan.FromMinutes(4).TotalSeconds;
                                       options.PICSRefreshDelaySeconds = 2;
                                       options.Webhooks = sendGraphTest && !string.IsNullOrWhiteSpace(testWebhookUrl)
                                           ? new List<WebhookFile>
                                           {
                                               new()
                                               {
                                                   type = "discord",
                                                   data = JsonSerializer.SerializeToElement(testWebhookUrl)
                                               }
                                           }
                                           : webhooks!;
                                   })
                                   .BindConfiguration("Steam")
                                   .ValidateDataAnnotations()
                                   .ValidateOnStart();

                           services.AddSingleton<Steam.Service>();
                           services.AddHostedService(sp => sp.GetRequiredService<Steam.Service>());
                       })
                       .Build();

        if (sendGraphTest)
        {
            var service = host.Services.GetRequiredService<Steam.Service>();
            await service.SendGraphTestAsync(useSampleData);
            return;
        }

        await host.RunAsync();
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}


