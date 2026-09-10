using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Envía mensajes usando la WhatsApp Cloud API de Meta (Graph API).
/// Docs: https://developers.facebook.com/docs/whatsapp/cloud-api/reference/messages
/// </summary>
public class WhatsAppService : IWhatsAppService
{
    private readonly HttpClient _http;
    private readonly ILogger<WhatsAppService> _logger;
    private readonly string _phoneNumberId;

    public WhatsAppService(HttpClient http, IConfiguration config, ILogger<WhatsAppService> logger)
    {
        _http = http;
        _logger = logger;

        var accessToken = config["WhatsApp:AccessToken"]
            ?? throw new InvalidOperationException("Falta WhatsApp:AccessToken en la configuración.");
        _phoneNumberId = config["WhatsApp:PhoneNumberId"]
            ?? throw new InvalidOperationException("Falta WhatsApp:PhoneNumberId en la configuración.");
        var apiVersion = config["WhatsApp:GraphApiVersion"] ?? "v21.0";

        _http.BaseAddress = new Uri($"https://graph.facebook.com/{apiVersion}/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public Task SendTextAsync(string toPhoneNumber, string body)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to = toPhoneNumber,
            type = "text",
            text = new { body }
        };
        return PostAsync(payload);
    }

    public Task SendButtonsAsync(string toPhoneNumber, string bodyText, IReadOnlyList<(string Id, string Title)> buttons)
    {
        // WhatsApp permite máximo 3 botones de respuesta rápida por mensaje.
        var payload = new
        {
            messaging_product = "whatsapp",
            to = toPhoneNumber,
            type = "interactive",
            interactive = new
            {
                type = "button",
                body = new { text = bodyText },
                action = new
                {
                    buttons = buttons.Take(3).Select(b => new
                    {
                        type = "reply",
                        reply = new { id = b.Id, title = b.Title }
                    })
                }
            }
        };
        return PostAsync(payload);
    }

    public Task SendListAsync(string toPhoneNumber, string bodyText, string buttonLabel,
        IReadOnlyList<(string Id, string Title, string? Description)> rows)
    {
        // WhatsApp permite hasta 10 filas en una lista interactiva.
        var payload = new
        {
            messaging_product = "whatsapp",
            to = toPhoneNumber,
            type = "interactive",
            interactive = new
            {
                type = "list",
                body = new { text = bodyText },
                action = new
                {
                    button = buttonLabel,
                    sections = new[]
                    {
                        new
                        {
                            title = "Opciones",
                            rows = rows.Take(10).Select(r => new
                            {
                                id = r.Id,
                                title = r.Title,
                                description = r.Description ?? ""
                            })
                        }
                    }
                }
            }
        };
        return PostAsync(payload);
    }

    private async Task PostAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{_phoneNumberId}/messages", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // No relanzar: un fallo al enviar no debe tumbar el webhook (Meta reintenta el POST entrante).
            _logger.LogError("Error enviando mensaje de WhatsApp ({Status}): {Body}", response.StatusCode, responseBody);
        }
    }
}
