using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

public interface ILeadRepository
{
    /// <summary>Guarda el lead capturado (nombre + criterios) en la tabla Leads.</summary>
    Task GuardarAsync(ConversationState state);
}
