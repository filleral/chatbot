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

    [JsonPropertyName("contacts")]
    public List<WhatsAppContact>? Contacts { get; set; }
}

public class WhatsAppContact
{
    [JsonPropertyName("wa_id")]
    public string WaId { get; set; } = "";

    [JsonPropertyName("profile")]
    public WhatsAppProfile? Profile { get; set; }
}

public class WhatsAppProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
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
    /// Lo que el usuario "seleccionó o escribió", normalizado (minúsculas, sin espacios extra):
    /// el id del botón/lista, o el texto libre en minúsculas. Sirve para reconocer opciones.
    /// </summary>
    public string GetUserInput() => GetRawInput().Trim().ToLowerInvariant();

    /// <summary>
    /// Igual que <see cref="GetUserInput"/> pero SIN pasar a minúsculas: para guardar
    /// respuestas de texto libre (nombre, barrio, valor esperado, trámite…) tal cual.
    /// </summary>
    public string GetRawInput()
    {
        if (Interactive?.ButtonReply is not null)
            return Interactive.ButtonReply.Id.Trim();

        if (Interactive?.ListReply is not null)
            return Interactive.ListReply.Id.Trim();

        if (Button is not null)
            return (Button.Payload ?? Button.Text ?? "").Trim();

        return (Text?.Body ?? "").Trim();
    }

    /// <summary>Título visible del botón/lista que tocó el usuario (para mostrarlo en el resumen).</summary>
    public string? GetSelectedTitle() =>
        Interactive?.ButtonReply?.Title ?? Interactive?.ListReply?.Title;
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
