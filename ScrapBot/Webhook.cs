using System.Text;
using System.Text.Json;


class Webhook
{
    public static Dictionary<string, IWebhook> impl = new() {
        {"content", new ContentWebhook()},
    };
}

public interface IWebhook
{
    public Task send(string content, JsonElement data);
}

public class ContentWebhook : IWebhook
{
    HttpClient httpClient = new();
    public async Task send(string content, JsonElement data)
    {
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
            var link = data.GetString();
            var res = await httpClient.PostAsync(link, jsonContent);
            res.Dispose();
        }
        catch (Exception err)
        {
            Console.WriteLine(err);
        }

    }
}

public class WebhookFile
{
    public required string type { get; set; }
    public required JsonElement data { get; set; }
}
