namespace WhatsappBot.Functions.Services;

/// <summary>Bitácora de mensajes entrantes y salientes (para el panel de control).</summary>
public interface IMessageLog
{
    Task RegistrarEntranteAsync(string phoneNumber, string tipo, string contenido);
    Task RegistrarSalienteAsync(string phoneNumber, string tipo, string contenido, bool ok, string? error);
}
