using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace WhatsappBot.Functions.Services;

public class PostgresMessageLog : IMessageLog
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresMessageLog> _logger;

    public PostgresMessageLog(IConfiguration config, ILogger<PostgresMessageLog> logger)
    {
        _connectionString = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
        _logger = logger;
    }

    public Task RegistrarEntranteAsync(string phoneNumber, string tipo, string contenido) =>
        InsertarAsync(phoneNumber, "in", tipo, contenido, null, null);

    public Task RegistrarSalienteAsync(string phoneNumber, string tipo, string contenido, bool ok, string? error) =>
        InsertarAsync(phoneNumber, "out", tipo, contenido, ok, error);

    private async Task InsertarAsync(string phone, string dir, string tipo, string contenido, bool? ok, string? error)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(@"
                INSERT INTO message_log (phone_number, direccion, tipo, contenido, ok, error)
                VALUES (@Phone, @Dir, @Tipo, @Contenido, @Ok, @Error);",
                new
                {
                    Phone = phone,
                    Dir = dir,
                    Tipo = tipo,
                    Contenido = Recortar(contenido, 4000),
                    Ok = ok,
                    Error = Recortar(error, 4000)
                });
        }
        catch (Exception ex)
        {
            // La bitácora nunca debe romper el flujo del bot.
            _logger.LogError(ex, "No se pudo registrar el mensaje ({Dir}) de {Phone}", dir, phone);
        }
    }

    private static string? Recortar(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
