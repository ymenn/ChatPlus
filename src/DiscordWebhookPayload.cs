using Newtonsoft.Json;

public class DiscordWebhookPayload(string content)
{
    [JsonProperty("content")]
    public string Content { get; set; } = content;
}
