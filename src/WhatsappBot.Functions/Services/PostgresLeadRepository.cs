using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>Persiste los leads en PostgreSQL (tabla leads de sql/schema.sql).</summary>
public class PostgresLeadRepository : ILeadRepository
{
    private readonly string _connectionString;

    private static bool _columnasOk;
    private static readonly SemaphoreSlim _migracionLock = new(1, 1);

    public PostgresLeadRepository(IConfiguration config)
    {
        _connectionString = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
    }

    public async Task GuardarAsync(ConversationState state)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await AsegurarColumnasAsync(conn);

        var detalle = state.Respuestas.Count > 0
            ? string.Join(" | ", state.Respuestas.Select(r => $"{r.Pregunta}: {r.Valor}"))
            : null;

        var inmuebles = state.PropiedadesMostradas.Count > 0
            ? string.Join(" ; ", state.PropiedadesMostradas)
            : null;

        await conn.ExecuteAsync(@"
            INSERT INTO leads
                (phone_number, nombre, tipo_interes, zona, presupuesto_rango, detalle, propiedades_mostradas, estado)
            VALUES
                (@Phone, @Nombre, @Flujo, @Zona, @Presupuesto, @Detalle, @Inmuebles, 'notificado');",
            new
            {
                Phone = state.PhoneNumber,
                Nombre = Limitar(state.NombreContacto, 300),
                Flujo = state.FlujoActivo,
                Zona = Limitar(state.Criteria.Zona, 300),
                Presupuesto = Limitar(state.Criteria.RangoPrecio, 100),
                Detalle = detalle,
                Inmuebles = inmuebles
            });
    }

    /// <summary>Añade las columnas nuevas si la tabla se creó con una versión anterior del esquema.</summary>
    private static async Task AsegurarColumnasAsync(NpgsqlConnection conn)
    {
        if (_columnasOk) return;
        await _migracionLock.WaitAsync();
        try
        {
            if (_columnasOk) return;
            await conn.ExecuteAsync(@"
                ALTER TABLE leads ADD COLUMN IF NOT EXISTS detalle text;
                ALTER TABLE leads ADD COLUMN IF NOT EXISTS propiedades_mostradas text;");
            _columnasOk = true;
        }
        finally
        {
            _migracionLock.Release();
        }
    }

    private static string? Limitar(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
