using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Guarda el estado de cada conversación en PostgreSQL (Neon u otro) para que el bot
/// "recuerde" en qué paso del menú va cada usuario, aunque la Function App se reinicie
/// entre mensajes.
/// </summary>
public class PostgresConversationStateService : IConversationStateService
{
    private readonly string _connectionString;

    public PostgresConversationStateService(IConfiguration config)
    {
        _connectionString = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
    }

    public async Task<ConversationState> GetOrCreateAsync(string phoneNumber)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<ConversationStateRow>(
            "SELECT phone_number, current_step, context_json FROM conversation_state WHERE phone_number = @phoneNumber",
            new { phoneNumber });

        if (row is null)
        {
            return new ConversationState { PhoneNumber = phoneNumber, CurrentStep = FlowStep.Inicio };
        }

        var state = new ConversationState
        {
            PhoneNumber = row.PhoneNumber,
            CurrentStep = row.CurrentStep
        };
        state.LoadContextFromJson(row.ContextJson);
        return state;
    }

    // DTO tipado para el resultado de la consulta (evita usar `dynamic` con Dapper).
    private class ConversationStateRow
    {
        public string PhoneNumber { get; set; } = "";
        public string CurrentStep { get; set; } = "";
        public string ContextJson { get; set; } = "{}";
    }

    public async Task SaveAsync(ConversationState state)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO conversation_state (phone_number, current_step, context_json, updated_at_utc)
            VALUES (@PhoneNumber, @CurrentStep, @ContextJson, now())
            ON CONFLICT (phone_number) DO UPDATE
                SET current_step   = EXCLUDED.current_step,
                    context_json   = EXCLUDED.context_json,
                    updated_at_utc = now();",
            new { state.PhoneNumber, state.CurrentStep, ContextJson = state.ContextToJson() });
    }

    public async Task ResetAsync(string phoneNumber)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM conversation_state WHERE phone_number = @phoneNumber", new { phoneNumber });
    }
}
