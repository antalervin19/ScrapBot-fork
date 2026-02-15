using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace ScrapBot;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        var webhookFile = (new StreamReader("./webhooks.json")).ReadToEnd();
        var webhooks = JsonSerializer.Deserialize<List<WebhookFile>>(webhookFile);

        if (webhooks == null)
        {
            Console.WriteLine("Couldn't find webhooks.json");
            return;
        }

        var host = Host.CreateDefaultBuilder(args)
                       .ConfigureServices((ctx, services) =>
                       {
                           services.AddOptions<Steam.Options>()
                                   .Configure(options =>
                                   {
                                       options.MaxReconnectDelaySeconds = (int)TimeSpan.FromMinutes(4).TotalSeconds;
                                       options.PICSRefreshDelaySeconds = 2;
                                       options.Webhooks = webhooks;
                                   })
                                   .BindConfiguration("Steam")
                                   .ValidateDataAnnotations()
                                   .ValidateOnStart();

                           services.AddHostedService<Steam.Service>();
                       })
                       .Build();

        await host.RunAsync();
    }
}


