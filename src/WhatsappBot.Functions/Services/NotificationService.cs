using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Avisa al asesor (o asesores) humano cuando un flujo termina, con el resumen de todo
/// lo que respondió el cliente. Manda un WhatsApp normal a cada número de
/// WhatsApp:AdvisorPhoneNumber (uno o varios, separados por coma).
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
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (_advisorPhoneNumbers.Length == 0)
            throw new InvalidOperationException("Falta WhatsApp:AdvisorPhoneNumber en la configuración.");
    }

    public async Task NotificarAsesorAsync(ConversationState state)
    {
        var respuestas = state.Respuestas.Count > 0
            ? "\n" + string.Join("\n", state.Respuestas.Select(r => $"• {r.Pregunta}: {r.Valor}"))
            : "";

        var inmuebles = state.PropiedadesMostradas.Count > 0
            ? "\n\n🏘️ Inmuebles mostrados:\n• " + string.Join("\n• ", state.PropiedadesMostradas)
            : "";

        var mensaje =
            "📩 *Nuevo lead por WhatsApp*\n" +
            $"👤 {Sinombre(state.NombreContacto)}\n" +
            $"📞 +{state.PhoneNumber}\n" +
            $"🗂️ Solicitud: {Flujo.Etiqueta(state.FlujoActivo)}\n" +
            respuestas +
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

    private static string Sinombre(string? n) => string.IsNullOrWhiteSpace(n) ? "(sin nombre)" : n;
}
