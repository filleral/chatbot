using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WhatsappBot.Functions.Flow;
using WhatsappBot.Functions.Models;
using WhatsappBot.Functions.Services;

// Dapper: mapear columnas snake_case de PostgreSQL (phone_number) a propiedades PascalCase (PhoneNumber).
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

// Render (y casi todos los PaaS) inyectan el puerto por la variable de entorno PORT.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient<IWhatsAppService, WhatsAppService>();
builder.Services.AddHttpClient<IPropertyCatalogService, PropertyCatalogService>();
builder.Services.AddScoped<IConversationStateService, PostgresConversationStateService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ILeadRepository, PostgresLeadRepository>();
builder.Services.AddScoped<FlowEngine>();

var app = builder.Build();

// Raíz / sonda de salud (Render la usa para saber que el servicio está vivo).
app.MapGet("/", () => "WhatsApp bot inmobiliaria — OK");

// GET /webhook  -> verificación del webhook (Meta la llama una sola vez al configurarlo).
app.MapGet("/webhook", (HttpRequest req, IConfiguration config, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Webhook");
    var expected = config["WhatsApp:VerifyToken"];

    var mode = req.Query["hub.mode"].ToString();
    var token = req.Query["hub.verify_token"].ToString();
    var challenge = req.Query["hub.challenge"].ToString();

    if (mode == "subscribe" && token == expected)
    {
        log.LogInformation("Webhook verificado correctamente por Meta.");
        return Results.Text(challenge);
    }

    log.LogWarning("Intento de verificación de webhook con token inválido.");
    return Results.StatusCode(StatusCodes.Status403Forbidden);
});

// POST /webhook -> aquí llegan los mensajes entrantes de WhatsApp.
app.MapPost("/webhook", async (HttpRequest req, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Webhook");

    string body;
    using (var reader = new StreamReader(req.Body))
    {
        body = await reader.ReadToEndAsync();
    }

    WhatsAppWebhookPayload? payload;
    try
    {
        payload = JsonSerializer.Deserialize<WhatsAppWebhookPayload>(body);
    }
    catch (JsonException ex)
    {
        log.LogError(ex, "Payload de webhook inválido: {Body}", body);
        return Results.Ok(); // 200 igual, para que Meta no siga reintentando.
    }

    // Solo mensajes en los que el usuario "dijo o eligió" algo: texto o respuesta a botón/lista.
    // Recibos de estado (entregado/leído) y otros tipos se ignoran sin tocar la base de datos.
    var lotes = (payload?.Entry.SelectMany(e => e.Changes).Select(c => c.Value)
                 ?? Enumerable.Empty<WhatsAppValue>()).ToList();

    var hayMensajes = lotes.Any(v => (v.Messages ?? new List<WhatsAppMessage>())
        .Any(m => m.Type is "text" or "interactive" or "button"));

    if (!hayMensajes)
    {
        return Results.Ok();
    }

    try
    {
        var flow = req.HttpContext.RequestServices.GetRequiredService<FlowEngine>();
        var store = req.HttpContext.RequestServices.GetRequiredService<IConversationStateService>();

        foreach (var value in lotes)
        {
            var nombrePerfil = value.Contacts?.FirstOrDefault()?.Profile?.Name;

            foreach (var mensaje in (value.Messages ?? new List<WhatsAppMessage>())
                         .Where(m => m.Type is "text" or "interactive" or "button"))
            {
                try
                {
                    var state = await store.GetOrCreateAsync(mensaje.From);
                    await flow.ProcesarMensajeAsync(
                        state,
                        mensaje.GetUserInput(),
                        mensaje.GetRawInput(),
                        mensaje.GetSelectedTitle(),
                        nombrePerfil);
                }
                catch (Exception ex)
                {
                    // Un error en un mensaje no debe tumbar el resto del batch ni el webhook.
                    log.LogError(ex, "Error procesando mensaje de {From}", mensaje.From);
                }
            }
        }
    }
    catch (Exception ex)
    {
        log.LogError(ex, "No se pudo inicializar el procesamiento del webhook (¿faltan variables de entorno?)");
    }

    // Meta espera un 200 rápido; si tardas mucho o devuelves error, reintenta el webhook.
    return Results.Ok();
});

app.Run();
