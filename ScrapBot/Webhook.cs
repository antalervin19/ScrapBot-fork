using System.Text;
using System.Text.Json;
using StoatSharp;


class Webhook
{
    public static Dictionary<string, IWebhook> impl = new() {
        {"discord", new DiscordWebhook()},
        {"stoat", new StoatWebhook()}
    };
}

public interface IWebhook
{
    public Task send(string content, JsonElement data);
    public Task sendGraph(string path, JsonElement data);
}

public class DiscordWebhook : IWebhook
{
    HttpClient httpClient = new();
    public async Task send(string content, JsonElement data)
    {
        var link = data.GetString();
        using StringContent jsonContent = new(
            JsonSerializer.Serialize(new
            {
                content = content,
            }),
            Encoding.UTF8,
            "application/json"
        );
        try
        {
            var res = await httpClient.PostAsync(link, jsonContent);
            res.Dispose();
        }
        catch (Exception err)
        {
            Console.WriteLine(err);
        }
    }

    public async Task sendGraph(string path, JsonElement data)
    {
        var link = data.GetString();
        if (!File.Exists(path))
        {
            Console.WriteLine($"File missing: {path}");
            return;
        }
        using var form = new MultipartFormDataContent();
        var fileStream = File.OpenRead(path);
        form.Add(new StreamContent(fileStream), "file", Path.GetFileName(path));
        try
        {
            var res = await httpClient.PostAsync("https://api.revolt.chat/channels/{target}/messages", form);
            res.Dispose();
        }
        catch (Exception err)
        {
            Console.WriteLine(err);
        }
        finally
        {
            fileStream.Dispose();
        }
    }
}


public class StoatWebhook : IWebhook
{
    StoatClient client = new(ClientMode.Http);
    public async Task send(string content, JsonElement data)
    {
        var stoatData = data.Deserialize<StoatWebhookData>()!;

        if (stoatData.botToken != client.Token)
        {
            await client.LoginAsync(stoatData.botToken, AccountType.Bot);
        }
        try
        {
            var channel = await client.Rest.GetChannelAsync(stoatData.channelId);
            if (channel == null)
            {
                Console.WriteLine("Invalid channel for stoat");
                return;
            }
            await channel.SendMessageAsync(content);
        }
        catch (Exception err)
        {
            Console.WriteLine(err);
        }
    }

    public async Task sendGraph(string path, JsonElement data)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"File missing: {path}");
            return;
        }
        var stoatData = data.Deserialize<StoatWebhookData>()!;
        if (stoatData.botToken != client.Token)
        {
            await client.LoginAsync(stoatData.botToken, AccountType.Bot);
        }
        try
        {
            var channel = await client.Rest.GetChannelAsync(stoatData.channelId);
            if (channel == null)
            {
                Console.WriteLine("Invalid channel for stoat");
                return;
            }
            await channel.SendFileAsync(path);
        }
        catch (Exception err)

        {
            Console.WriteLine(err);
        }
    }
}

public class AttachmentRes
{
    public required string id { get; set; }
}

public class StoatWebhookData
{
    public required string botToken { get; set; }
    public required string channelId { get; set; }
}


public class WebhookFile
{
    public required string type { get; set; }
    public required JsonElement data { get; set; }
}
