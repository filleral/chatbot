using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

public interface IConversationStateService
{
    Task<ConversationState> GetOrCreateAsync(string phoneNumber);
    Task SaveAsync(ConversationState state);
    Task ResetAsync(string phoneNumber);
}
