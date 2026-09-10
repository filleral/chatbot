using Microsoft.Extensions.Logging;
using WhatsappBot.Functions.Models;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Flow;

/// <summary>
/// El "cerebro" del bot: dado el estado actual de la conversación y lo que el usuario
/// acaba de responder (texto libre o el id de un botón/lista), decide qué mensaje
/// enviar y a qué paso pasar.
///
/// Es una máquina de estados simple a propósito. Para agregar una pregunta nueva:
///   1) agrega una constante en FlowStep,
///   2) agrega un "case" aquí que la maneje,
///   3) decide a qué paso avanza según la respuesta del usuario.
///
/// Flujo (menús, no texto libre con IA):
///   menú → arriendo|venta → zona → presupuesto → habitaciones
///        → hasta 3 inmuebles del sitio → ¿asesor? → nombre → aviso al asesor
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

    private static readonly string[] PalabrasAsesor =
        { "asesor", "hablar con asesor", "hablar_con_asesor", "hablar con un asesor" };

    private static readonly string[] PalabrasReinicio =
        { "menu", "menú", "inicio", "reiniciar", "empezar", "volver" };

    public async Task ProcesarMensajeAsync(ConversationState state, string userInput)
    {
        userInput = (userInput ?? "").Trim();

        // "Reiniciar": vuelve al menú principal desde cualquier punto.
        if (PalabrasReinicio.Contains(userInput.ToLowerInvariant()))
        {
            state.Criteria = new SearchCriteria();
            state.PropiedadesMostradas = new List<string>();
            state.NombreContacto = null;
            await EnviarMenuPrincipalAsync(state);
            return;
        }

        // Palabra de "escape": en cualquier punto, "asesor" saca al usuario del árbol
        // de preguntas y lo manda directo a hablar con una persona.
        if (PalabrasAsesor.Contains(userInput.ToLowerInvariant())
            && state.CurrentStep != FlowStep.PidiendoNombreParaAsesor)
        {
            state.CurrentStep = FlowStep.PidiendoNombreParaAsesor;
            await _stateStore.SaveAsync(state);
            await _whatsApp.SendTextAsync(state.PhoneNumber,
                "Claro, con gusto te comunico con un asesor. ¿Cuál es tu nombre?");
            return;
        }

        switch (state.CurrentStep)
        {
            case FlowStep.Inicio:
                await EnviarMenuPrincipalAsync(state);
                break;

            case FlowStep.MenuPrincipal:
                await ManejarMenuPrincipalAsync(state, userInput);
                break;

            case FlowStep.PidiendoZona:
                state.Criteria.Zona = InterpretarZona(userInput);
                state.CurrentStep = FlowStep.PidiendoPresupuesto;
                await _stateStore.SaveAsync(state);
                await EnviarPreguntaPresupuestoAsync(state);
                break;

            case FlowStep.PidiendoPresupuesto:
                if (!TryInterpretarPresupuesto(state.Criteria.Tipo, userInput, out var presupuesto))
                {
                    await _whatsApp.SendTextAsync(state.PhoneNumber, "Elige una de las opciones del menú, por favor. 🙏");
                    await EnviarPreguntaPresupuestoAsync(state);
                    break;
                }
                state.Criteria.RangoPrecio = presupuesto;
                state.CurrentStep = FlowStep.PidiendoHabitaciones;
                await _stateStore.SaveAsync(state);
                await EnviarPreguntaHabitacionesAsync(state);
                break;

            case FlowStep.PidiendoHabitaciones:
                if (userInput is not ("1" or "2" or "3+"))
                {
                    await _whatsApp.SendTextAsync(state.PhoneNumber, "Elige una de las opciones del menú, por favor. 🙏");
                    await EnviarPreguntaHabitacionesAsync(state);
                    break;
                }
                state.Criteria.Habitaciones = userInput;
                await MostrarResultadosAsync(state);
                break;

            case FlowStep.MostrandoResultados:
                await ManejarRespuestaResultadosAsync(state, userInput);
                break;

            case FlowStep.PidiendoNombreParaAsesor:
                await FinalizarConAsesorAsync(state, userInput);
                break;

            case FlowStep.Finalizado:
            default:
                // Si el usuario vuelve a escribir después de haber terminado, se reinicia el menú.
                state.Criteria = new SearchCriteria();
                state.PropiedadesMostradas = new List<string>();
                state.NombreContacto = null;
                await EnviarMenuPrincipalAsync(state);
                break;
        }
    }

    // ---------------------------------------------------------------- menú principal

    private async Task EnviarMenuPrincipalAsync(ConversationState state)
    {
        state.CurrentStep = FlowStep.MenuPrincipal;
        await _stateStore.SaveAsync(state);

        await _whatsApp.SendTextAsync(state.PhoneNumber,
            "¡Hola! 👋 Soy el asistente virtual de *Bienes Raíces White*. Te ayudo a encontrar tu inmueble.");

        await _whatsApp.SendListAsync(
            state.PhoneNumber,
            "¿Qué te gustaría hacer?",
            "Ver opciones",
            new (string, string, string?)[]
            {
                ("arriendo", "🏠 Tomar en arriendo", "Ver inmuebles disponibles para arrendar"),
                ("venta", "🏡 Comprar inmueble", "Ver inmuebles disponibles en venta"),
                ("asesor", "🗣️ Hablar con asesor", "Te contactamos directamente")
            });
    }

    private async Task ManejarMenuPrincipalAsync(ConversationState state, string userInput)
    {
        if (userInput is "arriendo" or "venta")
        {
            state.Criteria.Tipo = userInput;
            state.CurrentStep = FlowStep.PidiendoZona;
            await _stateStore.SaveAsync(state);
            await _whatsApp.SendButtonsAsync(state.PhoneNumber,
                "Perfecto. ¿En qué zona te gustaría buscar?\n\n" +
                "Si es otro municipio o un barrio puntual, escríbelo y lo tengo en cuenta.",
                new (string, string)[]
                {
                    ("zona_facatativa", "Facatativá"),
                    ("zona_otra_ciudad", "Otra ciudad"),
                    ("zona_cualquiera", "Cualquiera")
                });
            return;
        }

        // Entrada no reconocida: se repite el menú.
        await EnviarMenuPrincipalAsync(state);
    }

    // ---------------------------------------------------------------- zona

    private static string InterpretarZona(string userInput) => userInput.ToLowerInvariant() switch
    {
        "zona_facatativa" => "Facatativá",
        "zona_otra_ciudad" => "Otra ciudad",
        "zona_cualquiera" => "cualquiera",
        _ => userInput // texto libre: municipio o barrio escrito por el usuario
    };

    // ---------------------------------------------------------------- presupuesto

    private Task EnviarPreguntaPresupuestoAsync(ConversationState state)
    {
        var botones = state.Criteria.Tipo == "venta"
            ? new (string, string)[]
            {
                ("ven_lt150", "Menos de $150M"),
                ("ven_150_300", "$150M a $300M"),
                ("ven_gt300", "Más de $300M")
            }
            : new (string, string)[]
            {
                ("arr_lt1m", "Menos de $1.000.000"),
                ("arr_1_2m", "$1.000.000 a $2M"),
                ("arr_gt2m", "Más de $2.000.000")
            };

        return _whatsApp.SendButtonsAsync(state.PhoneNumber, "¿Cuál es tu presupuesto aproximado?", botones);
    }

    private static bool TryInterpretarPresupuesto(string? tipo, string userInput, out string etiqueta)
    {
        etiqueta = userInput.ToLowerInvariant() switch
        {
            "arr_lt1m" => "Menos de $1M/mes",
            "arr_1_2m" => "$1M–$2M/mes",
            "arr_gt2m" => "Más de $2M/mes",
            "ven_lt150" => "Menos de $150M",
            "ven_150_300" => "$150M–$300M",
            "ven_gt300" => "Más de $300M",
            _ => ""
        };
        return etiqueta.Length > 0;
    }

    // ---------------------------------------------------------------- habitaciones

    private Task EnviarPreguntaHabitacionesAsync(ConversationState state) =>
        _whatsApp.SendButtonsAsync(state.PhoneNumber, "¿Cuántas habitaciones necesitas?",
            new (string, string)[] { ("1", "1"), ("2", "2"), ("3+", "3 o más") });

    // ---------------------------------------------------------------- resultados

    private async Task MostrarResultadosAsync(ConversationState state)
    {
        state.CurrentStep = FlowStep.MostrandoResultados;

        IReadOnlyList<Property> resultados;
        try
        {
            resultados = await _catalogo.BuscarAsync(state.Criteria);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consultando el catálogo de inmuebles");
            resultados = Array.Empty<Property>();
        }

        state.PropiedadesMostradas = resultados
            .Select(r => $"{r.Titulo} — {r.Url}")
            .ToList();
        await _stateStore.SaveAsync(state);

        if (resultados.Count == 0)
        {
            await _whatsApp.SendTextAsync(state.PhoneNumber,
                "Por ahora no tengo inmuebles publicados que coincidan con esos criterios. " +
                "Un asesor puede ayudarte a encontrar opciones a la medida.");
        }
        else
        {
            await _whatsApp.SendTextAsync(state.PhoneNumber, "Encontré estas opciones para ti:");
            foreach (var propiedad in resultados)
            {
                await _whatsApp.SendTextAsync(state.PhoneNumber, propiedad.ComoTexto());
            }
        }

        await _whatsApp.SendButtonsAsync(state.PhoneNumber,
            "¿Quieres que un asesor te contacte para avanzar?",
            new (string, string)[] { ("si", "Sí, quiero asesor"), ("no", "No, gracias") });
    }

    private async Task ManejarRespuestaResultadosAsync(ConversationState state, string userInput)
    {
        switch (userInput.ToLowerInvariant())
        {
            case "si":
            case "sí":
                state.CurrentStep = FlowStep.PidiendoNombreParaAsesor;
                await _stateStore.SaveAsync(state);
                await _whatsApp.SendTextAsync(state.PhoneNumber, "¡Genial! ¿Cuál es tu nombre?");
                break;

            case "no":
                state.CurrentStep = FlowStep.Finalizado;
                await _stateStore.SaveAsync(state);
                await _whatsApp.SendTextAsync(state.PhoneNumber,
                    "Perfecto, quedo atento. Escribe *menú* cuando quieras buscar otro inmueble. 😊");
                break;

            default:
                await _whatsApp.SendButtonsAsync(state.PhoneNumber,
                    "¿Quieres que un asesor te contacte para avanzar?",
                    new (string, string)[] { ("si", "Sí, quiero asesor"), ("no", "No, gracias") });
                break;
        }
    }

    // ---------------------------------------------------------------- cierre con asesor

    private async Task FinalizarConAsesorAsync(ConversationState state, string nombre)
    {
        state.NombreContacto = string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim();
        state.CurrentStep = FlowStep.Finalizado;
        await _stateStore.SaveAsync(state);

        try
        {
            await _leads.GuardarAsync(state);
        }
        catch (Exception ex)
        {
            // Guardar el lead es deseable pero no debe impedir avisar al asesor.
            _logger.LogError(ex, "No se pudo guardar el lead de {Phone}", state.PhoneNumber);
        }

        await _notifications.NotificarAsesorAsync(state);

        await _whatsApp.SendTextAsync(state.PhoneNumber,
            $"¡Gracias{(state.NombreContacto is null ? "" : $", {state.NombreContacto}")}! " +
            "Ya avisé a uno de nuestros asesores con tus datos. Te va a escribir por este mismo WhatsApp en breve. 🙌");
    }
}
