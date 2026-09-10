namespace WhatsappBot.Functions.Services;

public interface IEmailSender
{
    /// <summary>true si hay SMTP y destinatario configurados.</summary>
    bool Configurado { get; }

    Task<(bool ok, string? error)> EnviarAsync(string asunto, string cuerpoHtml);
}
