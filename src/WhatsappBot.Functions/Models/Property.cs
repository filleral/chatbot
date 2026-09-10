namespace WhatsappBot.Functions.Models;

/// <summary>
/// Un inmueble tal como lo necesita el bot para mostrarlo en WhatsApp.
///
/// Los campos son los mismos que producía el flujo de n8n (nodo "Extraer Inmuebles"):
/// el listado público del sitio solo da título y enlace, y a partir del título se
/// deducen las habitaciones y, de ahí, la capacidad y el máximo de mascotas.
/// </summary>
public class Property
{
    /// <summary>Slug tomado de la URL de la ficha (identificador estable del inmueble).</summary>
    public string Id { get; set; } = "";

    public string Titulo { get; set; } = "";

    /// <summary>Link a la ficha en la página web.</summary>
    public string Url { get; set; } = "";

    /// <summary>Habitaciones deducidas del título (regla n8n).</summary>
    public int Habitaciones { get; set; }

    /// <summary>Capacidad máxima = habitaciones × 2 (regla n8n).</summary>
    public int CapacidadMaxima { get; set; }

    /// <summary>Mascotas máximas = min(habitaciones, 2) (regla n8n).</summary>
    public int MascotasMaximas { get; set; }

    /// <summary>"arriendo" | "venta" | "" — deducido de las palabras del título.</summary>
    public string Tipo { get; set; } = "";

    public string ComoTexto()
    {
        return $"🏠 *{Titulo}*\n" +
               $"🛏️ {Habitaciones} hab.  ·  👥 hasta {CapacidadMaxima} personas  ·  🐾 hasta {MascotasMaximas} mascotas\n" +
               $"🔗 {Url}";
    }
}

/// <summary>Criterios de búsqueda recolectados durante la conversación.</summary>
public class SearchCriteria
{
    public string? Tipo { get; set; }          // "arriendo" | "venta"
    public string? Zona { get; set; }           // texto libre o "cualquiera"
    public string? RangoPrecio { get; set; }    // etiqueta legible, ej: "$1M–$2M/mes"
    public string? Habitaciones { get; set; }   // "1" | "2" | "3+"
}
