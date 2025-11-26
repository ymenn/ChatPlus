using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ChatPlus.DiscordWebhookModels;

public class DiscordWebhookPayload
{
    [JsonProperty("embeds")]
    public List<Embed> Embeds { get; set; } = [];
}

public class Embed
{
    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("color")]
    public int? Color { get; set; } // Discord color (decimal representation of hex)

    [JsonProperty("fields")]
    public List<EmbedField> Fields { get; set; } = [];

    [JsonProperty("timestamp")]
    public DateTime? Timestamp { get; set; }
}

public class EmbedField
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("value")]
    public string? Value { get; set; }

    [JsonProperty("inline")]
    public bool? Inline { get; set; }
}
