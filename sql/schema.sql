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
    tipo_interes          text,                       -- 'arriendo' | 'venta'
    zona                  text,
    presupuesto_rango     text,
    habitaciones          text,                       -- '1' | '2' | '3+'
    propiedad_id_interes  text,
    estado                text        NOT NULL DEFAULT 'nuevo',  -- nuevo | notificado | atendido
    created_at_utc        timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_leads_created_at ON leads (created_at_utc DESC);

CREATE TABLE IF NOT EXISTS message_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    phone_number    text        NOT NULL,
    direction       text        NOT NULL,             -- 'in' | 'out'
    payload         text        NOT NULL,
    created_at_utc  timestamptz NOT NULL DEFAULT now()
);
