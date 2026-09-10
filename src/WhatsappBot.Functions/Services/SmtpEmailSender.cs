using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Manda el aviso de lead por correo con cualquier servidor SMTP.
/// Config (variables de entorno):
///   Email__SmtpHost   ej. smtp.gmail.com  |  smtp-relay.brevo.com  |  smtp.zoho.com
///   Email__SmtpPort   587 (STARTTLS, por defecto) o 465 (SSL)
///   Email__User       usuario SMTP (normalmente el mismo correo)
///   Email__Password   contraseña SMTP (en Gmail: una "contraseña de aplicación")
///   Email__From       remitente (por defecto = Email__User)
///   Email__To         uno o varios destinatarios separados por coma
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _pass;
    private readonly string _from;
    private readonly string[] _to;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger)
    {
        _logger = logger;
        _host = config["Email:SmtpHost"];
        _port = int.TryParse(config["Email:SmtpPort"], out var p) && p > 0 ? p : 587;
        _user = config["Email:User"];
        _pass = config["Email:Password"];
        _from = config["Email:From"] ?? _user ?? "";
        _to = (config["Email:To"] ?? "")
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool Configurado =>
        !string.IsNullOrWhiteSpace(_host) && !string.IsNullOrWhiteSpace(_from) && _to.Length > 0;

    public async Task<(bool ok, string? error)> EnviarAsync(string asunto, string cuerpoHtml)
    {
        if (!Configurado)
            return (false, "Correo no configurado (falta Email__SmtpHost / Email__To).");

        try
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse(_from));
            foreach (var destino in _to)
                msg.To.Add(MailboxAddress.Parse(destino));
            msg.Subject = asunto;
            msg.Body = new BodyBuilder { HtmlBody = cuerpoHtml }.ToMessageBody();

            using var client = new SmtpClient { Timeout = 20000 };
            await client.ConnectAsync(_host, _port,
                _port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);

            if (!string.IsNullOrWhiteSpace(_user))
                await client.AuthenticateAsync(_user, _pass);

            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando el correo de aviso");
            return (false, ex.Message);
        }
    }
}
