using Microsoft.Extensions.Logging;
using WhatsappBot.Functions.Models;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Flow;

/// <summary>
/// El "cerebro" del bot: una máquina de estados con los 8 flujos del menú de Bienes Raíces
/// White (los mismos que el workflow de n8n), guiando al usuario con botones y listas.
///
///   Menú (1-8)
///   ├─ 1 Arriendo        → personas → mascotas → documentos → ingresos → inmuebles → asesor
///   ├─ 2 Administración  → nº inmuebles → ocupación → ciudad → asesor
///   ├─ 3 Compra          → tipo → zona → presupuesto → uso → inmuebles → asesor
///   ├─ 4 Venta           → tipo → zona → valor esperado → asesor
///   ├─ 5 Asesoría venta  → tipo → precio/avalúo → asesor
///   ├─ 6 Notarial        → trámite → asesor
///   ├─ 7 Jurídico        → situación → asesor
///   └─ 8 Otro            → consulta → asesor
///
/// Cada flujo termina avisando a un asesor humano con el resumen de las respuestas.
/// </summary>
public class FlowEngine
{
    private readonly IWhatsAppService _whatsApp;
    private readonly IPropertyCatalogService _catalogo;
    private readonly INotificationService _notifications;
    private readonly IConversationStateService _stateStore;
    private readonly ILeadRepository _leads;
    private readonly ILogger<FlowEngine> _logger;

    public FlowEngine(
        IWhatsAppService whatsApp,
        IPropertyCatalogService catalogo,
        INotificationService notifications,
        IConversationStateService stateStore,
        ILeadRepository leads,
        ILogger<FlowEngine> logger)
    {
        _whatsApp = whatsApp;
        _catalogo = catalogo;
        _notifications = notifications;
        _stateStore = stateStore;
        _leads = leads;
        _logger = logger;
    }

    private static readonly string[] PalabrasReinicio =
        { "menu", "menú", "inicio", "reiniciar", "empezar", "volver", "menu principal", "menú principal" };

    public async Task ProcesarMensajeAsync(
        ConversationState state, string input, string inputCrudo, string? tituloSeleccionado, string? nombrePerfil)
    {
        input = (input ?? "").Trim();
        inputCrudo = (inputCrudo ?? "").Trim();

        // Nombre del perfil de WhatsApp (n8n no pedía el nombre, lo tomaba del contacto).
        if (string.IsNullOrWhiteSpace(state.NombreContacto) && !string.IsNullOrWhiteSpace(nombrePerfil))
            state.NombreContacto = nombrePerfil.Trim();

        // "menú" reinicia desde cualquier punto.
        if (PalabrasReinicio.Contains(input.ToLowerInvariant()))
        {
            state.Reiniciar();
            await EnviarMenuAsync(state);
            return;
        }

        switch (state.CurrentStep)
        {
            case FlowStep.Inicio:
                await EnviarMenuAsync(state);
                break;

            case FlowStep.Menu:
                await ManejarMenuAsync(state, input);
                break;

            case FlowStep.EnFlujo:
                await ContinuarFlujoAsync(state, input, inputCrudo, tituloSeleccionado);
                break;

            case FlowStep.MostrandoResultados:
                await ManejarRespuestaResultadosAsync(state, input);
                break;

            case FlowStep.PidiendoNombre:
                var nombre = inputCrudo.Trim();
                if (nombre.Length is < 2 or > 80 || EsIdDeBoton(nombre))
                {
                    await _whatsApp.SendTextAsync(state.PhoneNumber,
                        "Escríbeme tu nombre (solo el nombre) para pasarte con el asesor 🙂");
                    break;
                }
                state.NombreContacto = nombre;
                await FinalizarAsync(state);
                break;

            case FlowStep.Finalizado:
            default:
                await _whatsApp.SendTextAsync(state.PhoneNumber,
                    "Un asesor ya fue notificado y se comunicará contigo pronto. 🙏\n\n" +
                    "Escribe *menú* si quieres hacer otra consulta.");
                break;
        }
    }

    // ============================================================ MENÚ

    private async Task EnviarMenuAsync(ConversationState state, string? aviso = null)
    {
        state.CurrentStep = FlowStep.Menu;
        state.FlujoActivo = null;
        state.Paso = 0;
        await _stateStore.SaveAsync(state);

        var cuerpo =
            "¡Hola! Bienvenido a *Bienes Raíces White* 🏠\n¿En qué podemos ayudarte?\n\n" +
            "1️⃣ ¿Deseas tomar un inmueble en arriendo?\n" +
            "2️⃣ ¿Deseas que te administremos tu inmueble en arriendo?\n" +
            "3️⃣ ¿Deseas comprar un inmueble?\n" +
            "4️⃣ ¿Deseas vender un inmueble?\n" +
            "5️⃣ ¿Deseas obtener asesoría para la venta de tu inmueble?\n" +
            "6️⃣ ¿Deseas asesoría notarial?\n" +
            "7️⃣ ¿Deseas asesoría jurídica?\n" +
            "8️⃣ Otro";

        if (!string.IsNullOrEmpty(aviso))
            cuerpo = aviso + "\n\n" + cuerpo;

        await _whatsApp.SendListAsync(state.PhoneNumber, cuerpo, "Ver opciones",
            new (string, string, string?)[]
            {
                ("op_1", "1. Tomar en arriendo", null),
                ("op_2", "2. Administrar inmueble", null),
                ("op_3", "3. Comprar inmueble", null),
                ("op_4", "4. Vender inmueble", null),
                ("op_5", "5. Asesoría para vender", null),
                ("op_6", "6. Asesoría notarial", null),
                ("op_7", "7. Asesoría jurídica", null),
                ("op_8", "8. Otro", null),
            });
    }

    private async Task ManejarMenuAsync(ConversationState state, string input)
    {
        var opcion = MapearOpcion(input);
        if (opcion is null)
        {
            await EnviarMenuAsync(state, "No entendí. Elige una opción de la lista 👇");
            return;
        }

        state.FlujoActivo = Flujo.DesdeOpcion(opcion.Value);
        state.CurrentStep = FlowStep.EnFlujo;
        state.Paso = 1;

        if (state.FlujoActivo == Flujo.Arriendo) state.Criteria.Tipo = "arriendo";
        if (state.FlujoActivo == Flujo.Compra) state.Criteria.Tipo = "venta";

        await _stateStore.SaveAsync(state);
        await EnviarPreguntaActualAsync(state);
    }

    private static int? MapearOpcion(string input)
    {
        input = input.Trim();

        if (input.StartsWith("op_") && int.TryParse(input[3..], out var id) && id is >= 1 and <= 8)
            return id;

        if (input.Length == 1 && input[0] is >= '1' and <= '8')
            return input[0] - '0';

        string[] emojis = { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣" };
        for (var i = 0; i < emojis.Length; i++)
            if (input.Contains(emojis[i])) return i + 1;

        var t = Texto.Normalizar(input);
        if (t.Length == 0) return null;
        if (t.Contains("administr")) return 2;
        if (t.Contains("avaluo") || (t.Contains("asesoria") && (t.Contains("vend") || t.Contains("venta")))) return 5;
        if (t.Contains("notari")) return 6;
        if (t.Contains("juridic") || t.Contains("legal") || t.Contains("abogad") || t.Contains("demanda")) return 7;
        if (t.Contains("arriend") || t.Contains("arrendar") || t.Contains("alquil")) return 1;
        if (t.Contains("compr")) return 3;
        if (t.Contains("vend") || t.Contains("venta")) return 4;
        if (t.Contains("otro") || t.Contains("otra")) return 8;
        return null;
    }

    // ============================================================ PREGUNTAS DEL FLUJO

    private Task EnviarPreguntaActualAsync(ConversationState s) => (s.FlujoActivo, s.Paso) switch
    {
        // ---- 1 ARRIENDO ----
        (Flujo.Arriendo, 1) => Preguntar(s, "¿Para cuántas personas es el inmueble?",
            ("per_1", "1 persona"), ("per_2", "2 personas"), ("per_3", "3 personas"),
            ("per_4", "4 personas"), ("per_5", "5 o más")),
        (Flujo.Arriendo, 2) => Preguntar(s, "¿Tienes mascotas?",
            ("masc_0", "No"), ("masc_1", "Sí, 1"), ("masc_2", "Sí, 2 o más")),
        (Flujo.Arriendo, 3) => Preguntar(s,
            "📋 *Requisitos para arrendar:*\n" +
            "• Fotocopia de cédula\n• Certificado laboral vigente\n• Dos últimos desprendibles de nómina\n" +
            "• Extractos bancarios (últimos 3 meses)\n• Declaración de renta (si eres independiente)\n\n" +
            "⚠️ Tus ingresos deben ser el *doble* del canon. Los documentos *no* se reciben por WhatsApp.\n\n" +
            "¿Cuentas con estos documentos?",
            ("doc_si", "Sí, los tengo"), ("doc_no", "Aún no"), ("doc_indep", "Soy independiente")),
        (Flujo.Arriendo, 4) => Preguntar(s, "¿Tus ingresos mensuales son el doble del canon que deseas pagar?",
            ("ing_si", "Sí"), ("ing_no", "No"), ("ing_ns", "No estoy seguro")),

        // ---- 2 ADMINISTRACIÓN ----
        (Flujo.Administracion, 1) => Preguntar(s, "¿Cuántos inmuebles deseas que administremos?",
            ("adm_1", "1"), ("adm_2", "2"), ("adm_3", "3 o más")),
        (Flujo.Administracion, 2) => Preguntar(s, "¿Los inmuebles están ocupados actualmente o desocupados?",
            ("adm_ocu", "Ocupados"), ("adm_deso", "Desocupados"), ("adm_mix", "Unos y otros")),
        (Flujo.Administracion, 3) => Preguntar(s, "¿Están ubicados en Facatativá o en otra ciudad?",
            ("adm_fac", "Facatativá"), ("adm_otra", "Otra ciudad")),

        // ---- 3 COMPRA ----
        (Flujo.Compra, 1) => Preguntar(s, "¿Qué tipo de inmueble deseas comprar?",
            ("ti_apto", "Apartamento"), ("ti_casa", "Casa"), ("ti_local", "Local"),
            ("ti_lote", "Lote"), ("ti_otro", "Otro")),
        (Flujo.Compra, 2) => Preguntar(s, "¿En qué zona o barrio prefieres? (también puedes escribirlo)",
            ("zo_fac", "Facatativá"), ("zo_otra", "Otra ciudad"), ("zo_cualq", "Cualquiera")),
        (Flujo.Compra, 3) => Preguntar(s, "¿Cuál es tu presupuesto aproximado?",
            ("pr_lt150", "Menos de $150M"), ("pr_150_300", "$150M a $300M"), ("pr_gt300", "Más de $300M")),
        (Flujo.Compra, 4) => Preguntar(s, "¿Es para vivienda propia o como inversión?",
            ("uso_viv", "Vivienda propia"), ("uso_inv", "Inversión")),

        // ---- 4 VENTA ----
        (Flujo.Venta, 1) => Preguntar(s, "¿Qué tipo de inmueble deseas vender?",
            ("ti_apto", "Apartamento"), ("ti_casa", "Casa"), ("ti_local", "Local"),
            ("ti_lote", "Lote"), ("ti_otro", "Otro")),
        (Flujo.Venta, 2) => Preguntar(s, "¿En qué barrio o zona está ubicado? (escríbelo)"),
        (Flujo.Venta, 3) => Preguntar(s, "¿Cuál es el valor aproximado que esperas por el inmueble? (escríbelo)"),

        // ---- 5 ASESORÍA PARA VENTA ----
        (Flujo.AsesoriaVenta, 1) => Preguntar(s, "¿Qué tipo de inmueble tienes para vender?",
            ("ti_apto", "Apartamento"), ("ti_casa", "Casa"), ("ti_local", "Local"),
            ("ti_lote", "Lote"), ("ti_otro", "Otro")),
        (Flujo.AsesoriaVenta, 2) => Preguntar(s, "¿Ya tienes un precio definido o necesitas un avalúo?",
            ("av_precio", "Precio definido"), ("av_avaluo", "Necesito avalúo")),

        // ---- 6 NOTARIAL ----
        (Flujo.Notarial, 1) => Preguntar(s,
            "¿En qué trámite notarial necesitas ayuda? Escríbelo:\n\n" +
            "• Promesa de compraventa\n• Sucesión\n• Divorcio\n• Cancelación de patrimonio de familia\n• Otro"),

        // ---- 7 JURÍDICO ----
        (Flujo.Juridico, 1) => Preguntar(s,
            "Cuéntanos brevemente cuál es tu situación jurídica y un asesor te contactará."),

        // ---- 8 OTRO ----
        (Flujo.Otro, 1) => Preguntar(s,
            "Cuéntanos en qué podemos ayudarte y un asesor se comunicará contigo."),

        _ => _whatsApp.SendTextAsync(s.PhoneNumber, "Escribe *menú* para empezar de nuevo.")
    };

    private Task Preguntar(ConversationState s, string texto, params (string id, string title)[] opciones)
    {
        if (opciones.Length == 0)
            return _whatsApp.SendTextAsync(s.PhoneNumber, texto);

        if (opciones.Length <= 3)
            return _whatsApp.SendButtonsAsync(s.PhoneNumber, texto,
                opciones.Select(o => (o.id, o.title)).ToArray());

        return _whatsApp.SendListAsync(s.PhoneNumber, texto, "Elegir",
            opciones.Select(o => (o.id, o.title, (string?)null)).ToArray());
    }

    // ============================================================ RESPUESTAS DEL FLUJO

    private async Task ContinuarFlujoAsync(ConversationState s, string input, string crudo, string? titulo)
    {
        var etiqueta = titulo ?? crudo;               // lo que se le muestra al asesor
        var f = s.FlujoActivo;
        var p = s.Paso;
        var ok = true;

        switch (f, p)
        {
            // ---- ARRIENDO ----
            case (Flujo.Arriendo, 1):
                var personas = input switch
                {
                    "per_1" => 1, "per_2" => 2, "per_3" => 3, "per_4" => 4, "per_5" => 5,
                    _ => (int.TryParse(input, out var n) && n > 0) ? n : 0
                };
                if (personas == 0) { ok = false; break; }
                s.Criteria.Personas = personas;
                s.Registrar("Personas", personas >= 5 ? "5 o más" : personas.ToString());
                break;

            case (Flujo.Arriendo, 2):
                var mascotas = input switch { "masc_0" => 0, "masc_1" => 1, "masc_2" => 2, _ => -1 };
                if (mascotas < 0) { ok = false; break; }
                s.Criteria.Mascotas = mascotas;
                s.Registrar("Mascotas", mascotas == 0 ? "No" : mascotas == 2 ? "2 o más" : "1");
                break;

            case (Flujo.Arriendo, 3):
                if (input is not ("doc_si" or "doc_no" or "doc_indep")) { ok = false; break; }
                s.Registrar("¿Tiene los documentos?",
                    input switch { "doc_si" => "Sí", "doc_no" => "Aún no", _ => "Es independiente" });
                break;

            case (Flujo.Arriendo, 4):
                if (input is not ("ing_si" or "ing_no" or "ing_ns")) { ok = false; break; }
                s.Registrar("¿Ingresos 2× el canon?",
                    input switch { "ing_si" => "Sí", "ing_no" => "No", _ => "No está seguro" });
                break;

            // ---- ADMINISTRACIÓN ----
            case (Flujo.Administracion, 1):
                if (input is not ("adm_1" or "adm_2" or "adm_3") && !(int.TryParse(input, out var na) && na > 0))
                { ok = false; break; }
                s.Registrar("N.º de inmuebles", etiqueta);
                break;

            case (Flujo.Administracion, 2):
                if (input is not ("adm_ocu" or "adm_deso" or "adm_mix")) { ok = false; break; }
                s.Registrar("Ocupación", etiqueta);
                break;

            case (Flujo.Administracion, 3):
                if (input is not ("adm_fac" or "adm_otra")) { ok = false; break; }
                s.Registrar("Ubicación", etiqueta);
                break;

            // ---- COMPRA ----
            case (Flujo.Compra, 1):
                if (!EsTipoInmueble(input)) { ok = false; break; }
                s.Criteria.TipoInmueble = TipoInmuebleDesde(input);
                s.Registrar("Tipo de inmueble", etiqueta);
                break;

            case (Flujo.Compra, 2):
                s.Criteria.Zona = input switch
                {
                    "zo_fac" => "Facatativá",
                    "zo_otra" => "Otra ciudad",
                    "zo_cualq" => "cualquiera",
                    _ => crudo
                };
                if (string.IsNullOrWhiteSpace(s.Criteria.Zona)) { ok = false; break; }
                s.Registrar("Zona", s.Criteria.Zona == "cualquiera" ? "Cualquiera" : s.Criteria.Zona!);
                break;

            case (Flujo.Compra, 3):
                if (input is not ("pr_lt150" or "pr_150_300" or "pr_gt300")) { ok = false; break; }
                s.Criteria.RangoPrecio = etiqueta;
                s.Registrar("Presupuesto", etiqueta);
                break;

            case (Flujo.Compra, 4):
                if (input is not ("uso_viv" or "uso_inv")) { ok = false; break; }
                s.Registrar("Uso", etiqueta);
                break;

            // ---- VENTA ----
            case (Flujo.Venta, 1):
                if (!EsTipoInmueble(input)) { ok = false; break; }
                s.Registrar("Tipo de inmueble", etiqueta);
                break;
            case (Flujo.Venta, 2):
                if (string.IsNullOrWhiteSpace(crudo)) { ok = false; break; }
                s.Registrar("Zona / barrio", crudo);
                break;
            case (Flujo.Venta, 3):
                if (string.IsNullOrWhiteSpace(crudo)) { ok = false; break; }
                s.Registrar("Valor esperado", crudo);
                break;

            // ---- ASESORÍA VENTA ----
            case (Flujo.AsesoriaVenta, 1):
                if (!EsTipoInmueble(input)) { ok = false; break; }
                s.Registrar("Tipo de inmueble", etiqueta);
                break;
            case (Flujo.AsesoriaVenta, 2):
                if (input is not ("av_precio" or "av_avaluo")) { ok = false; break; }
                s.Registrar("Precio / avalúo", etiqueta);
                break;

            // ---- NOTARIAL / JURÍDICO / OTRO ----
            case (Flujo.Notarial, 1):
                if (string.IsNullOrWhiteSpace(crudo)) { ok = false; break; }
                s.Registrar("Trámite notarial", crudo);
                break;
            case (Flujo.Juridico, 1):
                if (string.IsNullOrWhiteSpace(crudo)) { ok = false; break; }
                s.Registrar("Situación jurídica", crudo);
                break;
            case (Flujo.Otro, 1):
                if (string.IsNullOrWhiteSpace(crudo)) { ok = false; break; }
                s.Registrar("Consulta", crudo);
                break;

            default:
                await EnviarMenuAsync(s);
                return;
        }

        if (!ok)
        {
            await _whatsApp.SendTextAsync(s.PhoneNumber, "Elige una de las opciones, por favor 🙏");
            await EnviarPreguntaActualAsync(s);
            return;
        }

        if (s.Paso < Flujo.TotalPasos(f))
        {
            s.Paso++;
            await _stateStore.SaveAsync(s);
            await EnviarPreguntaActualAsync(s);
            return;
        }

        // Fin de las preguntas del flujo.
        if (Flujo.MuestraInmuebles(f))
            await MostrarResultadosAsync(s);
        else
            await IrAAsesorAsync(s);
    }

    private static readonly System.Text.RegularExpressions.Regex IdBotonRegex =
        new(@"^(op|ti|adm|zo|pr|per|masc|doc|ing|uso|av|res)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool EsIdDeBoton(string s) => IdBotonRegex.IsMatch(s.Trim());

    private static bool EsTipoInmueble(string input) =>
        input is "ti_apto" or "ti_casa" or "ti_local" or "ti_lote" or "ti_otro";

    private static string TipoInmuebleDesde(string input) => input switch
    {
        "ti_apto" => "apartamento",
        "ti_casa" => "casa",
        "ti_local" => "local",
        "ti_lote" => "lote",
        _ => "otro"
    };

    // ============================================================ RESULTADOS (arriendo / compra)

    private async Task MostrarResultadosAsync(ConversationState s)
    {
        s.CurrentStep = FlowStep.MostrandoResultados;

        IReadOnlyList<Property> resultados;
        try
        {
            resultados = await _catalogo.BuscarAsync(s.Criteria);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consultando el catálogo de inmuebles");
            resultados = Array.Empty<Property>();
        }

        s.PropiedadesMostradas = resultados.Select(r => $"{r.Titulo} — {r.Url}").ToList();
        await _stateStore.SaveAsync(s);

        if (resultados.Count == 0)
        {
            await _whatsApp.SendTextAsync(s.PhoneNumber,
                "Por ahora no tengo inmuebles publicados que coincidan con tu perfil. " +
                "Un asesor puede ayudarte a buscar opciones a la medida.");
        }
        else
        {
            await _whatsApp.SendTextAsync(s.PhoneNumber, "Estas son las opciones que mejor encajan:");
            foreach (var p in resultados)
                await _whatsApp.SendTextAsync(s.PhoneNumber, p.ComoTexto());
        }

        await _whatsApp.SendButtonsAsync(s.PhoneNumber,
            "¿Quieres que un asesor te contacte para avanzar?",
            new (string, string)[] { ("res_si", "Sí, quiero asesor"), ("res_no", "No, gracias") });
    }

    private async Task ManejarRespuestaResultadosAsync(ConversationState s, string input)
    {
        switch (input.ToLowerInvariant())
        {
            case "res_si":
            case "si":
            case "sí":
                await IrAAsesorAsync(s);
                break;

            case "res_no":
            case "no":
                s.CurrentStep = FlowStep.Finalizado;
                await _stateStore.SaveAsync(s);
                await _whatsApp.SendTextAsync(s.PhoneNumber,
                    "Perfecto, quedo atento. Escribe *menú* cuando quieras hacer otra consulta. 😊");
                break;

            default:
                await _whatsApp.SendButtonsAsync(s.PhoneNumber,
                    "¿Quieres que un asesor te contacte para avanzar?",
                    new (string, string)[] { ("res_si", "Sí, quiero asesor"), ("res_no", "No, gracias") });
                break;
        }
    }

    // ============================================================ CIERRE CON ASESOR

    private async Task IrAAsesorAsync(ConversationState s)
    {
        if (!string.IsNullOrWhiteSpace(s.NombreContacto))
        {
            await FinalizarAsync(s);
            return;
        }

        s.CurrentStep = FlowStep.PidiendoNombre;
        await _stateStore.SaveAsync(s);
        await _whatsApp.SendTextAsync(s.PhoneNumber, "Para pasarte con un asesor, ¿cuál es tu nombre?");
    }

    private async Task FinalizarAsync(ConversationState s)
    {
        s.CurrentStep = FlowStep.Finalizado;
        await _stateStore.SaveAsync(s);

        try
        {
            await _leads.GuardarAsync(s);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el lead de {Phone}", s.PhoneNumber);
        }

        await _notifications.NotificarAsesorAsync(s);

        await _whatsApp.SendTextAsync(s.PhoneNumber,
            $"¡Perfecto{(string.IsNullOrWhiteSpace(s.NombreContacto) ? "" : $", {s.NombreContacto}")}! " +
            "Ya notificamos a uno de nuestros asesores con tus datos. " +
            "Se comunicará contigo por este mismo WhatsApp en breve. 🏠");
    }
}
