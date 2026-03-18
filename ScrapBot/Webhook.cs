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
        var link = data.GetString();
        if (content.StartsWith("7-Day Update Graph:") && content.Contains(".png"))
        {
            var filePath = content.Split(':', 2)[1].Trim();
            if (File.Exists(filePath))
            {
                using var form = new MultipartFormDataContent();
                var fileStream = File.OpenRead(filePath);
                form.Add(new StreamContent(fileStream), "file", Path.GetFileName(filePath));
                try
                {
                    var res = await httpClient.PostAsync(link, form);
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
                return;
            }
        }
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
}

public class WebhookFile
{
    public required string type { get; set; }
    public required JsonElement data { get; set; }
}
