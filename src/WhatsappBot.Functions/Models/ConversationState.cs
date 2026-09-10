using System.Text.Json;

namespace WhatsappBot.Functions.Models;

/// <summary>Pasos posibles del flujo. Agrega aquí nuevos pasos si extiendes el árbol.</summary>
public static class FlowStep
{
    public const string Inicio = "inicio";
    public const string MenuPrincipal = "menu_principal";
    public const string PidiendoZona = "pidiendo_zona";
    public const string PidiendoPresupuesto = "pidiendo_presupuesto";
    public const string PidiendoHabitaciones = "pidiendo_habitaciones";
    public const string MostrandoResultados = "mostrando_resultados";
    public const string PidiendoNombreParaAsesor = "pidiendo_nombre_para_asesor";
    public const string Finalizado = "finalizado";
}

/// <summary>Estado persistido en SQL por número de teléfono.</summary>
public class ConversationState
{
    public string PhoneNumber { get; set; } = "";
    public string CurrentStep { get; set; } = FlowStep.Inicio;
    public SearchCriteria Criteria { get; set; } = new();
    public string? NombreContacto { get; set; }

    /// <summary>Títulos (y links) de los inmuebles que se le mostraron al usuario,
    /// para incluirlos en el aviso al asesor aunque llegue en un mensaje posterior.</summary>
    public List<string> PropiedadesMostradas { get; set; } = new();

    public string ContextToJson() => JsonSerializer.Serialize(new ContextDto
    {
        Criteria = Criteria,
        NombreContacto = NombreContacto,
        PropiedadesMostradas = PropiedadesMostradas
    });

    /// <summary>Rehidrata el contexto guardado en SQL. Si el JSON es nuevo o inválido,
    /// se empieza de cero sin lanzar excepción.</summary>
    public void LoadContextFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<ContextDto>(json);
            if (dto is null) return;
            Criteria = dto.Criteria ?? new SearchCriteria();
            NombreContacto = dto.NombreContacto;
            PropiedadesMostradas = dto.PropiedadesMostradas ?? new List<string>();
        }
        catch { /* estado corrupto: se empieza de cero */ }
    }

    private class ContextDto
    {
        public SearchCriteria? Criteria { get; set; }
        public string? NombreContacto { get; set; }
        public List<string>? PropiedadesMostradas { get; set; }
    }
}
