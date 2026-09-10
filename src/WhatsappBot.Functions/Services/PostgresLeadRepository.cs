using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>Persiste los leads en PostgreSQL (tabla leads de sql/schema.sql).</summary>
public class PostgresLeadRepository : ILeadRepository
{
    private readonly string _connectionString;

    public PostgresLeadRepository(IConfiguration config)
    {
        _connectionString = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
    }

    public async Task GuardarAsync(ConversationState state)
    {
        var c = state.Criteria;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO leads
                (phone_number, nombre, tipo_interes, zona, presupuesto_rango, habitaciones, estado)
            VALUES
                (@PhoneNumber, @Nombre, @Tipo, @Zona, @Presupuesto, @Habitaciones, 'notificado');",
            new
            {
                state.PhoneNumber,
                Nombre = Limitar(state.NombreContacto, 300),
                Tipo = c.Tipo,
                Zona = Limitar(c.Zona, 300),
                Presupuesto = Limitar(c.RangoPrecio, 100),
                Habitaciones = Limitar(c.Habitaciones, 20)
            });
    }

    // Las columnas son 'text' (sin límite), pero recortamos por si llega algo absurdamente largo.
    private static string? Limitar(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
