using System.Text.Json;

namespace WhatsappBot.Functions.Models;

/// <summary>Estados generales de la conversación.</summary>
public static class FlowStep
{
    public const string Inicio = "inicio";
    public const string Menu = "menu";
    public const string EnFlujo = "en_flujo";                 // ejecutando las preguntas de un flujo
    public const string MostrandoResultados = "resultados";   // solo arriendo/compra: se mostraron inmuebles
    public const string PidiendoNombre = "pidiendo_nombre";
    public const string Finalizado = "finalizado";            // asesor ya notificado
}

/// <summary>Los 8 flujos del menú (mismos que el bot de n8n).</summary>
public static class Flujo
{
    public const string Arriendo = "arriendo";              // 1
    public const string Administracion = "administracion";  // 2
    public const string Compra = "compra";                  // 3
    public const string Venta = "venta";                    // 4
    public const string AsesoriaVenta = "asesoria_venta";   // 5
    public const string Notarial = "notarial";             // 6
    public const string Juridico = "juridico";             // 7
    public const string Otro = "otro";                     // 8

    public static string? DesdeOpcion(int op) => op switch
    {
        1 => Arriendo, 2 => Administracion, 3 => Compra, 4 => Venta,
        5 => AsesoriaVenta, 6 => Notarial, 7 => Juridico, 8 => Otro, _ => null
    };

    /// <summary>Cuántas preguntas tiene cada flujo antes de cerrar.</summary>
    public static int TotalPasos(string? flujo) => flujo switch
    {
        Arriendo => 4,
        Administracion => 3,
        Compra => 4,
        Venta => 3,
        AsesoriaVenta => 2,
        Notarial => 1,
        Juridico => 1,
        Otro => 1,
        _ => 0
    };

    /// <summary>Los flujos que muestran inmuebles antes de ofrecer asesor.</summary>
    public static bool MuestraInmuebles(string? flujo) => flujo is Arriendo or Compra;

    public static string Etiqueta(string? flujo) => flujo switch
    {
        Arriendo => "Arriendo",
        Administracion => "Administración de inmueble",
        Compra => "Compra",
        Venta => "Venta",
        AsesoriaVenta => "Asesoría para venta",
        Notarial => "Asesoría notarial",
        Juridico => "Asesoría jurídica",
        Otro => "Otro",
        _ => "Consulta"
    };
}

/// <summary>Una pregunta respondida, para el resumen que recibe el asesor.</summary>
public class Respuesta
{
    public string Pregunta { get; set; } = "";
    public string Valor { get; set; } = "";
}

/// <summary>Estado persistido en PostgreSQL por número de teléfono.</summary>
public class ConversationState
{
    public string PhoneNumber { get; set; } = "";
    public string CurrentStep { get; set; } = FlowStep.Inicio;

    /// <summary>Flujo activo (ver <see cref="Flujo"/>); null en el menú.</summary>
    public string? FlujoActivo { get; set; }

    /// <summary>Número de pregunta dentro del flujo (1, 2, 3, 4).</summary>
    public int Paso { get; set; }

    /// <summary>Respuestas capturadas, en orden, para pasárselas al asesor.</summary>
    public List<Respuesta> Respuestas { get; set; } = new();

    /// <summary>Criterios usados para filtrar inmuebles (arriendo / compra).</summary>
    public SearchCriteria Criteria { get; set; } = new();

    public string? NombreContacto { get; set; }

    /// <summary>Inmuebles que se le mostraron al usuario (para el aviso al asesor).</summary>
    public List<string> PropiedadesMostradas { get; set; } = new();

    public void Registrar(string pregunta, string valor) =>
        Respuestas.Add(new Respuesta { Pregunta = pregunta, Valor = valor });

    public void Reiniciar()
    {
        CurrentStep = FlowStep.Inicio;
        FlujoActivo = null;
        Paso = 0;
        Respuestas = new List<Respuesta>();
        Criteria = new SearchCriteria();
        PropiedadesMostradas = new List<string>();
        // NombreContacto se conserva (viene del perfil de WhatsApp).
    }

    public string ContextToJson() => JsonSerializer.Serialize(new ContextDto
    {
        FlujoActivo = FlujoActivo,
        Paso = Paso,
        Respuestas = Respuestas,
        Criteria = Criteria,
        NombreContacto = NombreContacto,
        PropiedadesMostradas = PropiedadesMostradas
    });

    public void LoadContextFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<ContextDto>(json);
            if (dto is null) return;
            FlujoActivo = dto.FlujoActivo;
            Paso = dto.Paso;
            Respuestas = dto.Respuestas ?? new List<Respuesta>();
            Criteria = dto.Criteria ?? new SearchCriteria();
            NombreContacto = dto.NombreContacto;
            PropiedadesMostradas = dto.PropiedadesMostradas ?? new List<string>();
        }
        catch { /* estado corrupto: se empieza de cero */ }
    }

    private class ContextDto
    {
        public string? FlujoActivo { get; set; }
        public int Paso { get; set; }
        public List<Respuesta>? Respuestas { get; set; }
        public SearchCriteria? Criteria { get; set; }
        public string? NombreContacto { get; set; }
        public List<string>? PropiedadesMostradas { get; set; }
    }
}
