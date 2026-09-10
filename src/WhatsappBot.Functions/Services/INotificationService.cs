using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

public interface INotificationService
{
    /// <summary>Avisa al asesor humano que hay un lead listo para atención personal.</summary>
    Task NotificarAsesorAsync(ConversationState state);
}
