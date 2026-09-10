using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Envía mensajes usando la WhatsApp Cloud API de Meta (Graph API) y deja constancia
/// de cada envío (y su resultado) en la bitácora <see cref="IMessageLog"/>.
/// Docs: https://developers.facebook.com/docs/whatsapp/cloud-api/reference/messages
/// </summary>
public class WhatsAppService : IWhatsAppService
{
    private readonly HttpClient _http;
    private readonly IMessageLog _log;
    private readonly ILogger<WhatsAppService> _logger;
    private readonly string _phoneNumberId;

    public WhatsAppService(HttpClient http, IConfiguration config, IMessageLog log, ILogger<WhatsAppService> logger)
    {
        _http = http;
        _log = log;
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
        return PostAsync(payload, toPhoneNumber, "texto", body);
    }

    public Task SendButtonsAsync(string toPhoneNumber, string bodyText, IReadOnlyList<(string Id, string Title)> buttons)
    {
        var usados = buttons.Take(3).ToList();
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
                    buttons = usados.Select(b => new
                    {
                        type = "reply",
                        reply = new { id = b.Id, title = b.Title }
                    })
                }
            }
        };
        var resumen = $"{bodyText}  [{string.Join(" / ", usados.Select(b => b.Title))}]";
        return PostAsync(payload, toPhoneNumber, "botones", resumen);
    }

    public Task SendListAsync(string toPhoneNumber, string bodyText, string buttonLabel,
        IReadOnlyList<(string Id, string Title, string? Description)> rows)
    {
        var usadas = rows.Take(10).ToList();
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
                            rows = usadas.Select(r => new
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
        var resumen = $"{bodyText}  [{string.Join(" / ", usadas.Select(r => r.Title))}]";
        return PostAsync(payload, toPhoneNumber, "lista", resumen);
    }

    private async Task PostAsync(object payload, string toPhoneNumber, string tipo, string contenido)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var ok = false;
        string? error = null;
        try
        {
            var response = await _http.PostAsync($"{_phoneNumberId}/messages", content);
            var responseBody = await response.Content.ReadAsStringAsync();
            ok = response.IsSuccessStatusCode;
            if (!ok)
            {
                error = $"HTTP {(int)response.StatusCode}: {responseBody}";
                _logger.LogError("Error enviando mensaje de WhatsApp: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "Excepción enviando mensaje de WhatsApp a {To}", toPhoneNumber);
        }

        await _log.RegistrarSalienteAsync(toPhoneNumber, tipo, contenido, ok, error);
    }
}
