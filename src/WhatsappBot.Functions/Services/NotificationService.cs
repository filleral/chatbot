using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Avisa al asesor (o asesores) humano cuando el flujo llega al final.
///
/// Implementación por defecto: manda un WhatsApp normal a cada número configurado en
/// WhatsApp:AdvisorPhoneNumber (uno o varios, separados por coma o punto y coma) con
/// el mismo WhatsAppService — no hace falta contratar nada extra. Si prefieres avisar
/// por correo (Azure Communication Services / SendGrid) o por un webhook de Teams,
/// agrega esa llamada aquí también.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<NotificationService> _logger;
    private readonly string[] _advisorPhoneNumbers;

    public NotificationService(IWhatsAppService whatsApp, IConfiguration config, ILogger<NotificationService> logger)
    {
        _whatsApp = whatsApp;
        _logger = logger;

        _advisorPhoneNumbers = (config["WhatsApp:AdvisorPhoneNumber"] ?? "")
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (_advisorPhoneNumbers.Length == 0)
            throw new InvalidOperationException("Falta WhatsApp:AdvisorPhoneNumber en la configuración.");
    }

    public async Task NotificarAsesorAsync(ConversationState state)
    {
        var c = state.Criteria;

        var inmuebles = state.PropiedadesMostradas.Count > 0
            ? "\n\n🏘️ Inmuebles mostrados:\n• " + string.Join("\n• ", state.PropiedadesMostradas)
            : "";

        var mensaje =
            $"📩 *Nuevo lead por WhatsApp*\n" +
            $"👤 Nombre: {state.NombreContacto ?? "(no indicado)"}\n" +
            $"📞 Teléfono: +{state.PhoneNumber}\n" +
            $"🎯 Interés: {c.Tipo ?? "-"}\n" +
            $"📍 Zona: {c.Zona ?? "-"}\n" +
            $"💰 Presupuesto: {c.RangoPrecio ?? "-"}\n" +
            $"🛏️ Habitaciones: {c.Habitaciones ?? "-"}" +
            inmuebles +
            $"\n\n➡️ Escríbele a +{state.PhoneNumber} para continuar la atención.";

        foreach (var numero in _advisorPhoneNumbers)
        {
            try
            {
                await _whatsApp.SendTextAsync(numero, mensaje);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo avisar al asesor {Numero}", numero);
            }
        }
    }
}
