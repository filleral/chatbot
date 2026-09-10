using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
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

// ---- Bot ----
builder.Services.AddSingleton<IMessageLog, PostgresMessageLog>();
builder.Services.AddSingleton<IDbInitializer, PostgresDbInitializer>();
builder.Services.AddHttpClient<IEmailSender, EmailSender>();
builder.Services.AddHttpClient<IWhatsAppService, WhatsAppService>();
builder.Services.AddHttpClient<IPropertyCatalogService, PropertyCatalogService>();
builder.Services.AddScoped<IConversationStateService, PostgresConversationStateService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ILeadRepository, PostgresLeadRepository>();
builder.Services.AddScoped<FlowEngine>();

// ---- WordPress -> Facebook / Instagram ----
builder.Services.AddScoped<ISocialPostRepository, PostgresSocialPostRepository>();
builder.Services.AddHttpClient<SocialPublisher>();

// Render (y cualquier proxy TLS) manda el esquema real en X-Forwarded-Proto.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// ---- Panel de control ----
builder.Services.AddScoped<PanelData>();
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Panel");
    o.Conventions.AllowAnonymousToPage("/Panel/Login");
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/panel/login";
        o.LogoutPath = "/panel/logout";
        o.AccessDeniedPath = "/panel/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        o.Cookie.Name = "brw_panel";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Crea/actualiza las tablas al arrancar.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<IDbInitializer>().EnsureAsync();
}

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Raíz / sonda de salud (Render la usa para saber que el servicio está vivo).
app.MapGet("/", () => Results.Redirect("/panel"));
app.MapGet("/health", () => "OK");

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
}).AllowAnonymous();

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

    var lotes = (payload?.Entry.SelectMany(e => e.Changes).Select(c => c.Value)
                 ?? Enumerable.Empty<WhatsAppValue>()).ToList();

    var hayMensajes = lotes.Any(v => (v.Messages ?? new List<WhatsAppMessage>())
        .Any(m => m.Type is "text" or "interactive" or "button"));
    var hayEstados = lotes.Any(v => v.Statuses is { Count: > 0 });

    if (!hayMensajes && !hayEstados)
    {
        return Results.Ok();
    }

    try
    {
        var sp = req.HttpContext.RequestServices;
        var msgLog = sp.GetRequiredService<IMessageLog>();
        var config = sp.GetRequiredService<IConfiguration>();

        // Números de asesor: sus fallos de entrega (ventana de 24 h) son esperados y van por correo,
        // así que no ensucian la página de Errores.
        var asesores = (config["WhatsApp:AdvisorPhoneNumber"] ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.TrimStart('+'))
            .ToHashSet();

        // Estados de entrega de los mensajes que el bot envió (sent/delivered/read/failed).
        foreach (var estado in lotes.SelectMany(v => v.Statuses ?? new List<WhatsAppStatus>()))
        {
            if (estado.Status is "failed" && !asesores.Contains(estado.RecipientId.TrimStart('+')))
            {
                var e = estado.Errors?.FirstOrDefault();
                var detalle = e is null
                    ? "Meta reportó el envío como fallido."
                    : $"[{e.Code}] {e.Title}: {e.Message ?? e.ErrorData?.Details}";
                await msgLog.RegistrarSalienteAsync(estado.RecipientId, "estado", "❌ no entregado al cliente", false, detalle);
            }
        }

        if (!hayMensajes)
        {
            return Results.Ok();
        }

        var flow = sp.GetRequiredService<FlowEngine>();
        var store = sp.GetRequiredService<IConversationStateService>();

        foreach (var value in lotes)
        {
            var nombrePerfil = value.Contacts?.FirstOrDefault()?.Profile?.Name;

            foreach (var mensaje in (value.Messages ?? new List<WhatsAppMessage>())
                         .Where(m => m.Type is "text" or "interactive" or "button"))
            {
                try
                {
                    var seleccion = mensaje.GetSelectedTitle();
                    await msgLog.RegistrarEntranteAsync(
                        mensaje.From,
                        seleccion is null ? "texto" : "interactivo",
                        seleccion ?? mensaje.GetRawInput());

                    var state = await store.GetOrCreateAsync(mensaje.From);
                    await flow.ProcesarMensajeAsync(
                        state,
                        mensaje.GetUserInput(),
                        mensaje.GetRawInput(),
                        seleccion,
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
}).AllowAnonymous();

// POST /wordpress/nuevo-inmueble -> WordPress avisa que se publicó un inmueble; se publica en FB/IG.
app.MapPost("/wordpress/nuevo-inmueble", async (HttpRequest req, IConfiguration config,
    SocialPublisher publisher, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("WordPress");

    string body;
    using (var reader = new StreamReader(req.Body))
        body = await reader.ReadToEndAsync();

    // Autenticación por secreto compartido (header o campo del body).
    var esperado = config["Social:WebhookSecret"];
    var recibido = req.Headers["X-Webhook-Secret"].ToString();

    long postId = 0;
    if (!string.IsNullOrWhiteSpace(body))
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            postId = LeerPostId(root);
            if (string.IsNullOrEmpty(recibido) && root.TryGetProperty("secret", out var sc))
                recibido = sc.GetString() ?? "";
        }
        catch (JsonException) { /* body no-JSON; se intenta con query string abajo */ }
    }
    if (postId == 0 && long.TryParse(req.Query["post_id"], out var qp)) postId = qp;
    if (postId == 0 && long.TryParse(req.Query["id"], out var qi)) postId = qi;

    if (!string.IsNullOrWhiteSpace(esperado) && recibido != esperado)
    {
        log.LogWarning("Webhook de WordPress con secreto inválido.");
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    if (postId <= 0)
        return Results.BadRequest(new { error = "Falta el id del post (post_id)." });

    try
    {
        var r = await publisher.PublicarAsync(postId);
        return Results.Ok(new { r.PostId, r.Estado, r.FbUrl, r.IgUrl, r.Imagenes, r.Error });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Error publicando el inmueble {PostId} en redes", postId);
        return Results.Ok(new { postId, estado = "error", error = ex.Message });
    }
});

app.Run();

static long LeerPostId(JsonElement root)
{
    foreach (var nombre in new[] { "post_id", "postId", "id", "ID" })
        if (root.TryGetProperty(nombre, out var v) && TryLong(v, out var n)) return n;

    if (root.TryGetProperty("post", out var post))
        foreach (var nombre in new[] { "ID", "id", "post_id" })
            if (post.TryGetProperty(nombre, out var v) && TryLong(v, out var n)) return n;

    return 0;

    static bool TryLong(JsonElement e, out long n)
    {
        n = 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt64(out n),
            JsonValueKind.String => long.TryParse(e.GetString(), out n),
            _ => false
        };
    }
}
