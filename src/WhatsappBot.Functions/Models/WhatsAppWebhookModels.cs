using System.Text.Json.Serialization;

namespace WhatsappBot.Functions.Models;

// Estructura mínima del payload que Meta envía al webhook cuando llega un mensaje.
// Referencia: https://developers.facebook.com/docs/whatsapp/cloud-api/webhooks/payload-examples

public class WhatsAppWebhookPayload
{
    [JsonPropertyName("entry")]
    public List<WhatsAppEntry> Entry { get; set; } = new();
}

public class WhatsAppEntry
{
    [JsonPropertyName("changes")]
    public List<WhatsAppChange> Changes { get; set; } = new();
}

public class WhatsAppChange
{
    [JsonPropertyName("value")]
    public WhatsAppValue Value { get; set; } = new();
}

public class WhatsAppValue
{
    [JsonPropertyName("messages")]
    public List<WhatsAppMessage>? Messages { get; set; }
}

public class WhatsAppMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = ""; // "text" | "interactive" | "button" ...

    [JsonPropertyName("text")]
    public WhatsAppText? Text { get; set; }

    [JsonPropertyName("interactive")]
    public WhatsAppInteractiveReply? Interactive { get; set; }

    [JsonPropertyName("button")]
    public WhatsAppButton? Button { get; set; }

    /// <summary>
    /// Devuelve lo que el usuario "seleccionó o escribió", ya sea texto libre
    /// o el id de un botón/lista, normalizado en minúsculas y sin espacios extra.
    /// </summary>
    public string GetUserInput()
    {
        if (Interactive?.ButtonReply is not null)
            return Interactive.ButtonReply.Id.Trim().ToLowerInvariant();

        if (Interactive?.ListReply is not null)
            return Interactive.ListReply.Id.Trim().ToLowerInvariant();

        if (Button is not null)
            return (Button.Payload ?? Button.Text ?? "").Trim().ToLowerInvariant();

        return (Text?.Body ?? "").Trim().ToLowerInvariant();
    }
}

public class WhatsAppText
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = "";
}

public class WhatsAppButton
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}

public class WhatsAppInteractiveReply
{
    [JsonPropertyName("button_reply")]
    public WhatsAppReplyOption? ButtonReply { get; set; }

    [JsonPropertyName("list_reply")]
    public WhatsAppReplyOption? ListReply { get; set; }
}

public class WhatsAppReplyOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
}
