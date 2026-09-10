using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>Fuente del catálogo de inmuebles que consulta el bot.</summary>
public interface IPropertyCatalogService
{
    /// <summary>Devuelve hasta 3 inmuebles que encajan con los criterios recolectados.</summary>
    Task<IReadOnlyList<Property>> BuscarAsync(SearchCriteria criteria);
}
