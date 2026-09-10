using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace WhatsappBot.Functions.Services;

/// <summary>Crea/actualiza las tablas al arrancar la app (todo idempotente).</summary>
public interface IDbInitializer
{
    Task EnsureAsync();
}

public class PostgresDbInitializer : IDbInitializer
{
    private const string Ddl = @"
CREATE TABLE IF NOT EXISTS conversation_state (
    phone_number    text        PRIMARY KEY,
    current_step    text        NOT NULL,
    context_json    text        NOT NULL DEFAULT '{}',
    updated_at_utc  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS leads (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    phone_number          text        NOT NULL,
    nombre                text,
    tipo_interes          text,
    zona                  text,
    presupuesto_rango     text,
    detalle               text,
    propiedades_mostradas text,
    estado                text        NOT NULL DEFAULT 'nuevo',
    created_at_utc        timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE leads ADD COLUMN IF NOT EXISTS detalle text;
ALTER TABLE leads ADD COLUMN IF NOT EXISTS propiedades_mostradas text;
CREATE INDEX IF NOT EXISTS ix_leads_created_at ON leads (created_at_utc DESC);

CREATE TABLE IF NOT EXISTS message_log (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    phone_number   text        NOT NULL,
    direccion      text        NOT NULL,
    tipo           text,
    contenido      text,
    ok             boolean,
    error          text,
    created_at_utc timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE message_log ADD COLUMN IF NOT EXISTS direccion text;
ALTER TABLE message_log ADD COLUMN IF NOT EXISTS tipo text;
ALTER TABLE message_log ADD COLUMN IF NOT EXISTS contenido text;
ALTER TABLE message_log ADD COLUMN IF NOT EXISTS ok boolean;
ALTER TABLE message_log ADD COLUMN IF NOT EXISTS error text;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'message_log' AND column_name = 'direction') THEN
        EXECUTE 'ALTER TABLE message_log ALTER COLUMN direction DROP NOT NULL';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'message_log' AND column_name = 'payload') THEN
        EXECUTE 'ALTER TABLE message_log ALTER COLUMN payload DROP NOT NULL';
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_msglog_phone   ON message_log (phone_number, created_at_utc);
CREATE INDEX IF NOT EXISTS ix_msglog_created ON message_log (created_at_utc DESC);

CREATE TABLE IF NOT EXISTS social_posts (
    post_id        bigint      PRIMARY KEY,
    titulo         text,
    link           text,
    imagenes       int         NOT NULL DEFAULT 0,
    fb_post_id     text,
    fb_url         text,
    ig_id          text,
    ig_url         text,
    estado         text        NOT NULL DEFAULT 'pendiente',
    error          text,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_social_updated ON social_posts (updated_at_utc DESC);

CREATE TABLE IF NOT EXISTS app_config (
    clave          text        PRIMARY KEY,
    valor          text,
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);
";

    private readonly string _connectionString;
    private readonly ILogger<PostgresDbInitializer> _logger;

    public PostgresDbInitializer(IConfiguration config, ILogger<PostgresDbInitializer> logger)
    {
        _connectionString = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
        _logger = logger;
    }

    public async Task EnsureAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(Ddl);
            _logger.LogInformation("Esquema de base de datos verificado.");
        }
        catch (Exception ex)
        {
            // No tumbar el arranque: si la BD está dormida o hay un problema puntual,
            // los repos harán su propio manejo de errores al primer uso.
            _logger.LogError(ex, "No se pudo verificar el esquema de la base de datos al arrancar.");
        }
    }
}
