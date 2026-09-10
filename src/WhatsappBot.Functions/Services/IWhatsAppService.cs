namespace WhatsappBot.Functions.Services;

public interface IWhatsAppService
{
    Task SendTextAsync(string toPhoneNumber, string body);

    Task SendButtonsAsync(string toPhoneNumber, string bodyText, IReadOnlyList<(string Id, string Title)> buttons);

    Task SendListAsync(string toPhoneNumber, string bodyText, string buttonLabel,
        IReadOnlyList<(string Id, string Title, string? Description)> rows);
}
