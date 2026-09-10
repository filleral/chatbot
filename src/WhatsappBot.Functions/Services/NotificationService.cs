using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Avisa al asesor/coordinación cuando un flujo termina, con el resumen de las respuestas.
///
///  1. WhatsApp a cada número de WhatsApp:AdvisorPhoneNumber — llega solo si ese número
///     tiene una conversación abierta con el bot en las últimas 24 h (regla de Meta).
///  2. Correo a Email:To — sin esa restricción, siempre llega. Es el canal fiable.
///
/// Ambos envíos quedan en la bitácora (message_log) para verlos en el panel.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IWhatsAppService _whatsApp;
    private readonly IEmailSender _email;
    private readonly IMessageLog _log;
    private readonly ILogger<NotificationService> _logger;
    private readonly string[] _advisorPhoneNumbers;

    public NotificationService(
        IWhatsAppService whatsApp,
        IEmailSender email,
        IMessageLog log,
        IConfiguration config,
        ILogger<NotificationService> logger)
    {
        _whatsApp = whatsApp;
        _email = email;
        _log = log;
        _logger = logger;

        _advisorPhoneNumbers = (config["WhatsApp:AdvisorPhoneNumber"] ?? "")
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (_advisorPhoneNumbers.Length == 0 && !_email.Configurado)
            _logger.LogWarning("Ningún canal de aviso configurado: los leads se guardan pero nadie recibe notificación. " +
                               "Configura WhatsApp:AdvisorPhoneNumber y/o Email:SmtpHost + Email:To.");
    }

    public async Task NotificarAsesorAsync(ConversationState state)
    {
        var texto = ConstruirTexto(state);

        // --- WhatsApp a los asesores ---
        foreach (var numero in _advisorPhoneNumbers)
        {
            try { await _whatsApp.SendTextAsync(numero, texto); }
            catch (Exception ex) { _logger.LogError(ex, "No se pudo avisar por WhatsApp a {Numero}", numero); }
        }

        // --- Correo (canal fiable) ---
        if (_email.Configurado)
        {
            var asunto = $"Nuevo lead WhatsApp · {Flujo.Etiqueta(state.FlujoActivo)} · " +
                         (string.IsNullOrWhiteSpace(state.NombreContacto) ? state.PhoneNumber : state.NombreContacto);
            var (ok, error) = await _email.EnviarAsync(asunto, ConstruirHtml(state));
            await _log.RegistrarSalienteAsync(state.PhoneNumber, "correo",
                "Aviso de lead por correo a coordinación", ok, error);
        }
    }

    private static string ConstruirTexto(ConversationState state)
    {
        var respuestas = state.Respuestas.Count > 0
            ? "\n" + string.Join("\n", state.Respuestas.Select(r => $"• {r.Pregunta}: {r.Valor}"))
            : "";

        var inmuebles = state.PropiedadesMostradas.Count > 0
            ? "\n\n🏘️ Inmuebles mostrados:\n• " + string.Join("\n• ", state.PropiedadesMostradas)
            : "";

        return "📩 *Nuevo lead por WhatsApp*\n" +
               $"👤 {Nombre(state.NombreContacto)}\n" +
               $"📞 +{state.PhoneNumber}\n" +
               $"🗂️ Solicitud: {Flujo.Etiqueta(state.FlujoActivo)}\n" +
               respuestas + inmuebles +
               $"\n\n➡️ Escríbele a +{state.PhoneNumber} para continuar la atención.";
    }

    private static string ConstruirHtml(ConversationState state)
    {
        static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

        var filas = string.Join("", state.Respuestas.Select(r =>
            $"<tr><td style='padding:4px 12px 4px 0;color:#667;'>{Enc(r.Pregunta)}</td><td style='padding:4px 0;'><strong>{Enc(r.Valor)}</strong></td></tr>"));

        var inmuebles = state.PropiedadesMostradas.Count > 0
            ? "<p style='margin:16px 0 4px;color:#667;'>Inmuebles mostrados</p><ul style='margin:0;padding-left:18px;'>" +
              string.Join("", state.PropiedadesMostradas.Select(i => $"<li>{Enc(i)}</li>")) + "</ul>"
            : "";

        return $@"
<div style='font-family:-apple-system,Segoe UI,Roboto,sans-serif;max-width:560px;color:#1a1a1a;'>
  <h2 style='margin:0 0 4px;'>Nuevo lead por WhatsApp</h2>
  <p style='margin:0 0 16px;color:#8a1f42;font-weight:600;'>{Enc(Flujo.Etiqueta(state.FlujoActivo))}</p>
  <table style='border-collapse:collapse;font-size:14px;'>
    <tr><td style='padding:4px 12px 4px 0;color:#667;'>Contacto</td><td style='padding:4px 0;'><strong>{Enc(Nombre(state.NombreContacto))}</strong></td></tr>
    <tr><td style='padding:4px 12px 4px 0;color:#667;'>Teléfono</td><td style='padding:4px 0;'><strong>+{Enc(state.PhoneNumber)}</strong></td></tr>
    {filas}
  </table>
  {inmuebles}
  <p style='margin:20px 0 0;'>
    <a href='https://wa.me/{Enc(state.PhoneNumber)}' style='background:#8a1f42;color:#fff;padding:9px 16px;border-radius:8px;text-decoration:none;'>Escribirle por WhatsApp</a>
  </p>
  <p style='margin:14px 0 0;font-size:12px;color:#999;'>Enviado automáticamente por el bot de Bienes Raíces White.</p>
</div>";
    }

    private static string Nombre(string? n) => string.IsNullOrWhiteSpace(n) ? "(sin nombre)" : n;
}
