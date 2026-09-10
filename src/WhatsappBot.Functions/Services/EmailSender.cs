using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Manda el aviso de lead por correo usando una API HTTPS (Render bloquea el SMTP saliente).
/// Detecta el proveedor por el prefijo de la clave:
///   Email__ApiKey = re_...        -> Resend  (https://resend.com, 3000/mes gratis)
///   Email__ApiKey = xkeysib-...   -> Brevo   (https://brevo.com, 300/día gratis)
///
/// Otras variables:
///   Email__To        destinatario(s), separados por coma  (obligatorio)
///   Email__From      remitente. En Resend puede omitirse (usa onboarding@resend.dev).
///                    En Brevo debe ser un remitente verificado en tu cuenta.
///   Email__FromName  nombre visible del remitente (opcional)
/// </summary>
public class EmailSender : IEmailSender
{
    private enum Proveedor { Ninguno, Resend, Brevo }

    private readonly HttpClient _http;
    private readonly ILogger<EmailSender> _logger;
    private readonly string? _apiKey;
    private readonly string? _from;
    private readonly string _fromName;
    private readonly string[] _to;
    private readonly Proveedor _prov;

    public EmailSender(HttpClient http, IConfiguration config, ILogger<EmailSender> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = config["Email:ApiKey"];
        _from = config["Email:From"];
        _fromName = config["Email:FromName"] ?? "Bienes Raíces White";
        _to = (config["Email:To"] ?? "")
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _prov = _apiKey switch
        {
            not null when _apiKey.StartsWith("re_", StringComparison.Ordinal) => Proveedor.Resend,
            not null when _apiKey.StartsWith("xkeysib-", StringComparison.Ordinal) => Proveedor.Brevo,
            _ => Proveedor.Ninguno
        };

        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool Configurado =>
        _prov != Proveedor.Ninguno &&
        _to.Length > 0 &&
        (_prov == Proveedor.Resend || !string.IsNullOrWhiteSpace(_from));

    public async Task<(bool ok, string? error)> EnviarAsync(string asunto, string cuerpoHtml)
    {
        if (!Configurado)
            return (false, "Correo no configurado (falta Email__ApiKey / Email__To, o Email__From en Brevo).");

        try
        {
            using var req = _prov == Proveedor.Resend
                ? ConstruirResend(asunto, cuerpoHtml)
                : ConstruirBrevo(asunto, cuerpoHtml);

            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            return resp.IsSuccessStatusCode
                ? (true, null)
                : (false, $"HTTP {(int)resp.StatusCode}: {Recortar(body, 400)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando el correo de aviso ({Prov})", _prov);
            return (false, ex.Message);
        }
    }

    private HttpRequestMessage ConstruirResend(string asunto, string html)
    {
        var remitente = string.IsNullOrWhiteSpace(_from)
            ? $"{_fromName} <onboarding@resend.dev>"
            : $"{_fromName} <{_from}>";

        var payload = new
        {
            from = remitente,
            to = _to,
            subject = asunto,
            html
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        return req;
    }

    private HttpRequestMessage ConstruirBrevo(string asunto, string html)
    {
        var payload = new
        {
            sender = new { email = _from, name = _fromName },
            to = _to.Select(t => new { email = t }).ToArray(),
            subject = asunto,
            htmlContent = html
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("api-key", _apiKey);
        return req;
    }

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..max];
}
