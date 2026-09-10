-- Esquema para PostgreSQL (Neon, Supabase, CockroachDB, Postgres local...).
-- Ejecutar una vez sobre la base de datos del bot.
--   Neon:   pégalo en el "SQL Editor" de la consola de Neon y dale Run.
--   psql:   psql "TU_CONNECTION_STRING" -f sql/schema.sql

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
    tipo_interes          text,                       -- flujo elegido: arriendo | administracion | compra | venta | ...
    zona                  text,
    presupuesto_rango     text,
    detalle               text,                       -- resumen de todas las respuestas del flujo
    propiedades_mostradas text,                       -- inmuebles que se le mostraron, si aplica
    estado                text        NOT NULL DEFAULT 'nuevo',  -- nuevo | notificado | atendido
    created_at_utc        timestamptz NOT NULL DEFAULT now()
);

-- Si la tabla ya existía con el esquema anterior, añade las columnas nuevas:
ALTER TABLE leads ADD COLUMN IF NOT EXISTS detalle text;
ALTER TABLE leads ADD COLUMN IF NOT EXISTS propiedades_mostradas text;

CREATE INDEX IF NOT EXISTS ix_leads_created_at ON leads (created_at_utc DESC);

CREATE TABLE IF NOT EXISTS message_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    phone_number    text        NOT NULL,
    direction       text        NOT NULL,             -- 'in' | 'out'
    payload         text        NOT NULL,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
