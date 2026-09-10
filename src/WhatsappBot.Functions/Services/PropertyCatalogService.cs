using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WhatsappBot.Functions.Models;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Obtiene el catálogo de inmuebles del MISMO modo que lo hacía el flujo de n8n:
///
///   1) descarga las páginas del listado público del sitio
///      (https://bienesraiceswhite.co/inmuebles/ , /page/2/ , ...),
///   2) por cada ficha extrae título y enlace con la misma expresión regular
///      que usaba el nodo "Extraer Inmuebles",
///   3) deduce las habitaciones a partir del título con las mismas reglas
///      (número explícito → dúplex = 2 → apartamento/casa = 2 → 1 por defecto),
///   4) capacidad = habitaciones × 2  y  mascotas = min(habitaciones, 2).
///
/// El sitio no expone una API estructurada, así que precio y zona no vienen en el
/// listado: se recogen en la conversación para pasárselos al asesor, pero el filtro
/// real se hace por tipo (arriendo/venta) y habitaciones; la zona filtra de forma
/// suave contra el texto del título.
///
/// El resultado se cachea en memoria unos minutos para no descargar el sitio en
/// cada mensaje.
/// </summary>
public class PropertyCatalogService : IPropertyCatalogService
{
    // Misma regex del nodo "Extraer Inmuebles" de n8n.
    private static readonly Regex FichaRegex = new(
        @"<h2[^>]*>\s*<a[^>]*href=""([^""]+)""[^>]*>([\s\S]+?)</a>\s*</h2>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HabitacionesRegex = new(
        @"(\d+)\s*(hab|habitaci[oó]n|cuarto|alcoba)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagsRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex EspaciosRegex = new(@"\s+", RegexOptions.Compiled);

    // Caché a nivel de proceso (compartida entre invocaciones de la Function).
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static IReadOnlyList<Property> _cache = Array.Empty<Property>();
    private static DateTimeOffset _cacheExpiraUtc = DateTimeOffset.MinValue;

    private readonly HttpClient _http;
    private readonly ILogger<PropertyCatalogService> _logger;
    private readonly string _listingUrl;
    private readonly int _maxPaginas;
    private readonly TimeSpan _cacheDuracion;

    public PropertyCatalogService(HttpClient http, IConfiguration config, ILogger<PropertyCatalogService> logger)
    {
        _http = http;
        _logger = logger;

        _listingUrl = (config["PropertyScraper:ListingUrl"] ?? "https://bienesraiceswhite.co/inmuebles/")
            .Trim().TrimEnd('/');
        _maxPaginas = int.TryParse(config["PropertyScraper:MaxPages"], out var mp) && mp > 0 ? mp : 3;
        var minutos = int.TryParse(config["PropertyScraper:CacheMinutes"], out var cm) && cm >= 0 ? cm : 10;
        _cacheDuracion = TimeSpan.FromMinutes(minutos);

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; WhatsAppBotInmobiliaria/1.0)");
        }
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<IReadOnlyList<Property>> BuscarAsync(SearchCriteria criteria)
    {
        var catalogo = await ObtenerCatalogoAsync();
        if (catalogo.Count == 0) return catalogo;

        IEnumerable<Property> q = catalogo;

        // Tipo: exacto; los inmuebles cuyo tipo no se pudo deducir no se descartan.
        if (criteria.Tipo is "arriendo" or "venta")
        {
            q = q.Where(p => p.Tipo == criteria.Tipo || string.IsNullOrEmpty(p.Tipo));
        }

        var porTipo = q.ToList();

        // Habitaciones: mínimo pedido. Si no queda ninguno, se relaja el filtro.
        var min = HabitacionesMinimas(criteria.Habitaciones);
        if (min > 0)
        {
            var porHab = porTipo.Where(p => p.Habitaciones >= min).ToList();
            q = porHab.Count > 0 ? porHab : porTipo;
        }
        else
        {
            q = porTipo;
        }

        // Zona: filtro suave contra el título. Si nadie coincide, no descarta nada.
        if (!string.IsNullOrWhiteSpace(criteria.Zona) &&
            !string.Equals(criteria.Zona, "cualquiera", StringComparison.OrdinalIgnoreCase))
        {
            var zonaNorm = Normalizar(criteria.Zona);
            if (zonaNorm.Length >= 3)
            {
                var porZona = q.Where(p => Normalizar(p.Titulo).Contains(zonaNorm)).ToList();
                if (porZona.Count > 0) q = porZona;
            }
        }

        return q.Take(3).ToList();
    }

    private static int HabitacionesMinimas(string? h) => h switch
    {
        "1" => 1,
        "2" => 2,
        "3+" or "3" or "4" or "4+" or "5" => 3,
        _ => 0
    };

    private async Task<IReadOnlyList<Property>> ObtenerCatalogoAsync()
    {
        if (DateTimeOffset.UtcNow < _cacheExpiraUtc && _cache.Count > 0)
            return _cache;

        await CacheLock.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow < _cacheExpiraUtc && _cache.Count > 0)
                return _cache;

            var propiedades = await DescargarYParsearAsync();
            if (propiedades.Count > 0)
            {
                _cache = propiedades;
                _cacheExpiraUtc = DateTimeOffset.UtcNow.Add(_cacheDuracion);
                return propiedades;
            }

            // La descarga falló o no devolvió nada: se reutiliza lo último bueno si existe.
            return _cache;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private async Task<IReadOnlyList<Property>> DescargarYParsearAsync()
    {
        var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resultado = new List<Property>();

        for (var pagina = 1; pagina <= _maxPaginas; pagina++)
        {
            var url = pagina == 1 ? $"{_listingUrl}/" : $"{_listingUrl}/page/{pagina}/";

            string html;
            try
            {
                using var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Listado {Url} respondió {Status}", url, resp.StatusCode);
                    break;
                }
                html = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error descargando {Url}", url);
                break;
            }

            var nuevasEnPagina = 0;
            foreach (Match m in FichaRegex.Matches(html))
            {
                var link = m.Groups[1].Value.Trim();
                var titulo = EspaciosRegex.Replace(
                    WebUtility.HtmlDecode(TagsRegex.Replace(m.Groups[2].Value, "")), " ").Trim();

                if (titulo.Length <= 3) continue;
                if (!vistas.Add(link)) continue;

                nuevasEnPagina++;
                var hab = DeducirHabitaciones(titulo);
                resultado.Add(new Property
                {
                    Id = SlugDesdeUrl(link),
                    Titulo = titulo,
                    Url = link,
                    Habitaciones = hab,
                    CapacidadMaxima = hab * 2,
                    MascotasMaximas = Math.Min(hab, 2),
                    Tipo = DeducirTipo(titulo)
                });
            }

            // Si una página no aportó fichas nuevas, no hay más páginas útiles.
            if (nuevasEnPagina == 0) break;
        }

        _logger.LogInformation("Catálogo scrapeado: {Total} inmuebles desde {Url}", resultado.Count, _listingUrl);
        return resultado;
    }

    // Mismas reglas que el nodo "Extraer Inmuebles" de n8n.
    private static int DeducirHabitaciones(string titulo)
    {
        var m = HabitacionesRegex.Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
            return n;

        var t = titulo.ToLowerInvariant();
        if (t.Contains("duplex") || t.Contains("dúplex")) return 2;
        if (t.Contains("apartamento") || t.Contains("apto") || t.Contains("casa")) return 2;
        return 1;
    }

    private static string DeducirTipo(string titulo)
    {
        var t = Normalizar(titulo);
        if (t.Contains("arriend") || t.Contains("arrienda") || t.Contains("alquil") || t.Contains("renta"))
            return "arriendo";
        if (t.Contains("vend") || t.Contains("venta"))
            return "venta";
        return "";
    }

    private static string SlugDesdeUrl(string url)
    {
        var partes = url.Trim().TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length > 0 ? partes[^1] : url;
    }

    private static string Normalizar(string s)
    {
        s = s.ToLowerInvariant().Trim();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
