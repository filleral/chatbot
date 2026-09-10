using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

// ---- modelos de lectura para el panel ----

public record PanelStats(
    int Conversaciones,
    int LeadsHoy,
    int LeadsSemana,
    int LeadsPendientes,
    int ErroresUlt24h,
    int MensajesHoy,
    IReadOnlyList<FlujoConteo> PorFlujo);

public record FlujoConteo(string Flujo, int Total);

public record ConversacionResumen(
    string Telefono,
    string? Nombre,
    string CurrentStep,
    string? Flujo,
    int Paso,
    int TotalMensajes,
    DateTime UltimaActividadUtc);

public record MensajeRow(
    long Id,
    string Telefono,
    string Direccion,
    string? Tipo,
    string? Contenido,
    bool? Ok,
    string? Error,
    DateTime CreadoUtc);

public record ConversacionDetalle(
    ConversacionResumen Resumen,
    IReadOnlyList<Respuesta> Respuestas,
    IReadOnlyList<string> Inmuebles,
    IReadOnlyList<MensajeRow> Mensajes);

public record LeadRow(
    long Id,
    DateTime CreadoUtc,
    string Telefono,
    string? Nombre,
    string? Flujo,
    string? Zona,
    string? Presupuesto,
    string? Detalle,
    string? Inmuebles,
    string Estado);

/// <summary>Consultas de solo lectura (más una) que alimentan el panel de control.</summary>
public class PanelData
{
    private readonly string _cs;

    private static readonly TimeZoneInfo Bogota = ResolverZona();

    public PanelData(IConfiguration config)
    {
        _cs = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
    }

    public static DateTime ToBogota(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Bogota);

    public async Task<PanelStats> StatsAsync()
    {
        await using var conn = new NpgsqlConnection(_cs);
        using var multi = await conn.QueryMultipleAsync(@"
            SELECT count(*) FROM conversation_state;
            SELECT count(*) FROM leads WHERE created_at_utc >= date_trunc('day', now());
            SELECT count(*) FROM leads WHERE created_at_utc >= now() - interval '7 days';
            SELECT count(*) FROM leads WHERE estado <> 'atendido';
            SELECT count(*) FROM message_log WHERE direccion = 'out' AND ok IS FALSE
                   AND created_at_utc >= now() - interval '24 hours';
            SELECT count(*) FROM message_log WHERE created_at_utc >= date_trunc('day', now());
            SELECT coalesce(tipo_interes, '(sin flujo)') AS flujo, count(*) AS total
                   FROM leads GROUP BY 1 ORDER BY 2 DESC;");

        var conversaciones = await multi.ReadSingleAsync<int>();
        var leadsHoy = await multi.ReadSingleAsync<int>();
        var leadsSemana = await multi.ReadSingleAsync<int>();
        var pendientes = await multi.ReadSingleAsync<int>();
        var errores = await multi.ReadSingleAsync<int>();
        var mensajesHoy = await multi.ReadSingleAsync<int>();
        var porFlujo = (await multi.ReadAsync<(string flujo, int total)>())
            .Select(x => new FlujoConteo(x.flujo, x.total)).ToList();

        return new PanelStats(conversaciones, leadsHoy, leadsSemana, pendientes, errores, mensajesHoy, porFlujo);
    }

    public async Task<IReadOnlyList<ConversacionResumen>> ConversacionesAsync(string? filtro)
    {
        await using var conn = new NpgsqlConnection(_cs);
        var f = string.IsNullOrWhiteSpace(filtro) ? null : $"%{filtro.Trim()}%";
        var filas = await conn.QueryAsync<(string phone, string step, string ctx, DateTime upd, int msgs)>(@"
            SELECT cs.phone_number, cs.current_step, cs.context_json, cs.updated_at_utc,
                   (SELECT count(*) FROM message_log m WHERE m.phone_number = cs.phone_number) AS msgs
            FROM conversation_state cs
            WHERE (@f IS NULL OR cs.phone_number ILIKE @f OR cs.context_json ILIKE @f)
            ORDER BY cs.updated_at_utc DESC
            LIMIT 300;", new { f });

        return filas.Select(r =>
        {
            var st = Rehidratar(r.ctx);
            return new ConversacionResumen(r.phone, st.NombreContacto, r.step, st.FlujoActivo, st.Paso, r.msgs, r.upd);
        }).ToList();
    }

    public async Task<ConversacionDetalle?> ConversacionAsync(string telefono)
    {
        await using var conn = new NpgsqlConnection(_cs);
        var cab = await conn.QuerySingleOrDefaultAsync<(string phone, string step, string ctx, DateTime upd)?>(@"
            SELECT phone_number, current_step, context_json, updated_at_utc
            FROM conversation_state WHERE phone_number = @t;", new { t = telefono });

        if (cab is null) return null;

        var st = Rehidratar(cab.Value.ctx);
        var msgs = (await conn.QueryAsync<MensajeRow>(@"
            SELECT id AS Id, phone_number AS Telefono, direccion AS Direccion, tipo AS Tipo,
                   contenido AS Contenido, ok AS Ok, error AS Error, created_at_utc AS CreadoUtc
            FROM message_log WHERE phone_number = @t
            ORDER BY created_at_utc ASC, id ASC
            LIMIT 500;", new { t = telefono })).ToList();

        var msgCount = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM message_log WHERE phone_number = @t;", new { t = telefono });

        var resumen = new ConversacionResumen(
            cab.Value.phone, st.NombreContacto, cab.Value.step, st.FlujoActivo, st.Paso, msgCount, cab.Value.upd);

        return new ConversacionDetalle(resumen, st.Respuestas, st.PropiedadesMostradas, msgs);
    }

    public async Task<IReadOnlyList<LeadRow>> LeadsAsync(string? flujo, string? estado)
    {
        await using var conn = new NpgsqlConnection(_cs);
        return (await conn.QueryAsync<LeadRow>(@"
            SELECT id AS Id, created_at_utc AS CreadoUtc, phone_number AS Telefono, nombre AS Nombre,
                   tipo_interes AS Flujo, zona AS Zona, presupuesto_rango AS Presupuesto,
                   detalle AS Detalle, propiedades_mostradas AS Inmuebles, estado AS Estado
            FROM leads
            WHERE (@flujo IS NULL OR tipo_interes = @flujo)
              AND (@estado IS NULL OR estado = @estado)
            ORDER BY created_at_utc DESC
            LIMIT 500;",
            new { flujo = Nullify(flujo), estado = Nullify(estado) })).ToList();
    }

    public async Task MarcarLeadAsync(long id, string estado)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.ExecuteAsync("UPDATE leads SET estado = @estado WHERE id = @id;",
            new { id, estado });
    }

    public async Task<IReadOnlyList<MensajeRow>> ErroresAsync()
    {
        await using var conn = new NpgsqlConnection(_cs);
        return (await conn.QueryAsync<MensajeRow>(@"
            SELECT id AS Id, phone_number AS Telefono, direccion AS Direccion, tipo AS Tipo,
                   contenido AS Contenido, ok AS Ok, error AS Error, created_at_utc AS CreadoUtc
            FROM message_log
            WHERE direccion = 'out' AND ok IS FALSE
            ORDER BY created_at_utc DESC
            LIMIT 200;")).ToList();
    }

    private static ConversationState Rehidratar(string? ctx)
    {
        var st = new ConversationState();
        st.LoadContextFromJson(ctx);
        return st;
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TimeZoneInfo ResolverZona()
    {
        foreach (var id in new[] { "America/Bogota", "SA Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* siguiente */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
    }
}
