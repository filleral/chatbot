using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WhatsappBot.Functions.Services;

/// <summary>
/// Publica un inmueble de WordPress en Facebook e Instagram — lo mismo que hacía el
/// workflow "WordPress a Redes Sociales" de n8n:
///
///   1. Lee el post del WP REST API: título, contenido y galería de fotos (gallery_urls).
///   2. Facebook: sube las fotos (sin publicar) y crea una publicación con todas.
///   3. Instagram: crea un carrusel (o una sola foto) y lo publica.
///   4. Guarda el resultado para verlo en el panel y evitar publicar dos veces el mismo post.
/// </summary>
public class SocialPublisher
{
    private static readonly Regex TagsRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ShortcodeRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex EspaciosRegex = new(@"[ \t]*\n[ \t]*\n[ \t]*(\n[ \t]*)+", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<SocialPublisher> _logger;
    private readonly ISocialPostRepository _repo;

    private readonly string? _token;
    private readonly string? _fbPage;
    private readonly string? _igUser;
    private readonly string _wpBase;
    private readonly string _postType;
    private readonly string _ver;
    private readonly int _maxImgs;
    private readonly int _maxChars;

    public SocialPublisher(HttpClient http, IConfiguration config, ISocialPostRepository repo,
        ILogger<SocialPublisher> logger)
    {
        _http = http;
        _repo = repo;
        _logger = logger;

        _token = config["Social:MetaAccessToken"];
        _fbPage = config["Social:FacebookPageId"];
        _igUser = config["Social:InstagramUserId"];
        _wpBase = (config["Social:WpBaseUrl"] ?? "https://bienesraiceswhite.co").TrimEnd('/');
        _postType = config["Social:PostType"] ?? "em_portfolio";
        _ver = config["Social:GraphApiVersion"] ?? "v21.0";
        _maxImgs = int.TryParse(config["Social:MaxImagenes"], out var mi) && mi is > 0 and <= 10 ? mi : 10;
        _maxChars = int.TryParse(config["Social:ContenidoMaxChars"], out var mc) && mc > 0 ? mc : 400;

        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public bool Configurado =>
        !string.IsNullOrWhiteSpace(_token) &&
        (!string.IsNullOrWhiteSpace(_fbPage) || !string.IsNullOrWhiteSpace(_igUser));

    public async Task<SocialPost> PublicarAsync(long postId, bool forzar = false)
    {
        var previo = await _repo.ObtenerAsync(postId);
        if (!forzar && previo is { Estado: "publicado" })
            return previo;

        if (!Configurado)
        {
            var r = Resultado(postId, previo, "error", error: "Faltan variables Social__MetaAccessToken / PageId / InstagramUserId.");
            await _repo.GuardarAsync(r);
            return r;
        }

        WpPost? post;
        try
        {
            post = await ObtenerPostWpAsync(postId);
        }
        catch (Exception ex)
        {
            var r = Resultado(postId, previo, "error", error: $"No se pudo leer el post de WordPress: {ex.Message}");
            await _repo.GuardarAsync(r);
            return r;
        }

        if (post is null)
        {
            var r = Resultado(postId, previo, "error", error: "El post no existe en el WP REST API.");
            await _repo.GuardarAsync(r);
            return r;
        }

        if (!string.Equals(post.Status, "publish", StringComparison.OrdinalIgnoreCase))
        {
            var r = Resultado(postId, previo, "omitido", titulo: post.Titulo, link: post.Link,
                error: $"El post está en estado '{post.Status}', no 'publish'.");
            await _repo.GuardarAsync(r);
            return r;
        }

        var texto = ConstruirTexto(post);
        var imagenes = post.Imagenes.Take(_maxImgs).ToList();

        string? fbId = null, fbUrl = null, igId = null, igUrl = null;
        string? error = null;

        if (!string.IsNullOrWhiteSpace(_fbPage))
        {
            try { (fbId, fbUrl) = await PublicarFacebookAsync(texto, imagenes); }
            catch (Exception ex) { error = $"Facebook: {ex.Message}"; _logger.LogError(ex, "Fallo publicando en Facebook"); }
        }

        if (!string.IsNullOrWhiteSpace(_igUser) && imagenes.Count > 0)
        {
            try { (igId, igUrl) = await PublicarInstagramAsync(texto, imagenes); }
            catch (Exception ex)
            {
                error = (error is null ? "" : error + " | ") + $"Instagram: {ex.Message}";
                _logger.LogError(ex, "Fallo publicando en Instagram");
            }
        }
        else if (!string.IsNullOrWhiteSpace(_igUser) && imagenes.Count == 0)
        {
            error = (error is null ? "" : error + " | ") + "Instagram: el inmueble no tiene fotos.";
        }

        var estado = (fbId is not null || igId is not null)
            ? (error is null ? "publicado" : "parcial")
            : "error";

        var resultado = new SocialPost(postId, post.Titulo, post.Link, imagenes.Count,
            fbId, fbUrl, igId, igUrl, estado, error, previo?.CreadoUtc ?? DateTime.UtcNow, DateTime.UtcNow);

        await _repo.GuardarAsync(resultado);
        _logger.LogInformation("Publicación redes post {PostId}: {Estado}", postId, estado);
        return resultado;
    }

    public Task<IReadOnlyList<SocialPost>> ListarAsync() => _repo.ListarAsync();

    /// <summary>Publica a partir de un id numérico o de la URL del inmueble (resuelve el slug).</summary>
    public async Task<SocialPost> PublicarPorEntradaAsync(string entrada)
    {
        entrada = (entrada ?? "").Trim();
        if (entrada.Length == 0) throw new ArgumentException("Escribe un id o una URL.");

        if (long.TryParse(entrada, out var id))
            return await PublicarAsync(id, forzar: true);

        var slug = entrada.Split('?')[0].TrimEnd('/').Split('/').LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("No pude sacar el id ni el slug de eso.");

        using var resp = await _http.GetAsync(
            $"{_wpBase}/wp-json/wp/v2/{_postType}?slug={Uri.EscapeDataString(slug)}&_fields=id");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 &&
            doc.RootElement[0].TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var encontrado))
            return await PublicarAsync(encontrado, forzar: true);

        throw new ArgumentException($"No encontré un inmueble con el slug '{slug}'.");
    }

    // -------------------------------------------------- WordPress

    private record WpPost(string Titulo, string ContenidoHtml, string Link, string Status, List<string> Imagenes);

    private async Task<WpPost?> ObtenerPostWpAsync(long id)
    {
        using var resp = await _http.GetAsync($"{_wpBase}/wp-json/wp/v2/{_postType}/{id}");
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var titulo = WebUtility.HtmlDecode(TextoDe(root, "title")).Trim();
        var contenido = TextoDe(root, "content");
        var link = root.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "";
        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "publish";

        var imgs = new List<string>();
        if (root.TryGetProperty("gallery_urls", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            imgs.AddRange(g.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim()));
        }

        // Respaldo: imagen destacada si no hay galería.
        if (imgs.Count == 0 && root.TryGetProperty("featured_media", out var fm) &&
            fm.TryGetInt64(out var mediaId) && mediaId > 0)
        {
            try
            {
                using var mr = await _http.GetAsync($"{_wpBase}/wp-json/wp/v2/media/{mediaId}?_fields=source_url");
                if (mr.IsSuccessStatusCode)
                {
                    using var md = JsonDocument.Parse(await mr.Content.ReadAsStringAsync());
                    if (md.RootElement.TryGetProperty("source_url", out var su) && su.GetString() is { } url)
                        imgs.Add(url);
                }
            }
            catch { /* sin respaldo */ }
        }

        return new WpPost(titulo, contenido, link, status, imgs);
    }

    private static string TextoDe(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var p) && p.TryGetProperty("rendered", out var r)
            ? r.GetString() ?? ""
            : "";

    private string ConstruirTexto(WpPost post)
    {
        var limpio = WebUtility.HtmlDecode(ShortcodeRegex.Replace(TagsRegex.Replace(post.ContenidoHtml, " "), ""));
        limpio = EspaciosRegex.Replace(limpio, "\n\n").Trim();
        limpio = Regex.Replace(limpio, @"[ \t]{2,}", " ");

        if (limpio.Length > _maxChars)
            limpio = limpio[.._maxChars].TrimEnd() + "…";

        var partes = new List<string> { post.Titulo };
        if (limpio.Length > 0) partes.Add(limpio);
        if (!string.IsNullOrWhiteSpace(post.Link)) partes.Add($"Ver inmueble: {post.Link}");
        return string.Join("\n\n", partes);
    }

    // -------------------------------------------------- Facebook

    private async Task<(string id, string url)> PublicarFacebookAsync(string texto, List<string> imgs)
    {
        var fbids = new List<string>();
        foreach (var img in imgs)
        {
            var r = await GraphPostAsync($"{_fbPage}/photos", new()
            {
                ["url"] = img,
                ["published"] = "false",
                ["access_token"] = _token!
            });
            if (r.TryGetProperty("id", out var id) && id.GetString() is { } pid)
                fbids.Add(pid);
        }

        var form = new Dictionary<string, string>
        {
            ["message"] = texto,
            ["access_token"] = _token!
        };
        if (fbids.Count > 0)
            form["attached_media"] = JsonSerializer.Serialize(fbids.Select(id => new { media_fbid = id }));

        var post = await GraphPostAsync($"{_fbPage}/feed", form);
        var postId = post.GetProperty("id").GetString()!;
        return (postId, $"https://www.facebook.com/{postId}");
    }

    // -------------------------------------------------- Instagram

    private async Task<(string id, string url)> PublicarInstagramAsync(string texto, List<string> imgs)
    {
        string creationId;

        if (imgs.Count == 1)
        {
            var c = await GraphPostAsync($"{_igUser}/media", new()
            {
                ["image_url"] = imgs[0],
                ["caption"] = texto,
                ["access_token"] = _token!
            });
            creationId = c.GetProperty("id").GetString()!;
            await EsperarContenedorAsync(creationId);
        }
        else
        {
            var hijos = new List<string>();
            foreach (var img in imgs.Take(10))
            {
                var c = await GraphPostAsync($"{_igUser}/media", new()
                {
                    ["image_url"] = img,
                    ["is_carousel_item"] = "true",
                    ["access_token"] = _token!
                });
                hijos.Add(c.GetProperty("id").GetString()!);
            }
            foreach (var h in hijos) await EsperarContenedorAsync(h);

            var carrusel = await GraphPostAsync($"{_igUser}/media", new()
            {
                ["media_type"] = "CAROUSEL",
                ["children"] = string.Join(",", hijos),
                ["caption"] = texto,
                ["access_token"] = _token!
            });
            creationId = carrusel.GetProperty("id").GetString()!;
            await EsperarContenedorAsync(creationId);
        }

        var pub = await GraphPostAsync($"{_igUser}/media_publish", new()
        {
            ["creation_id"] = creationId,
            ["access_token"] = _token!
        });
        var mediaId = pub.GetProperty("id").GetString()!;

        var url = "https://www.instagram.com/";
        try
        {
            var perma = await GraphGetAsync(mediaId, new() { ["fields"] = "permalink", ["access_token"] = _token! });
            if (perma.TryGetProperty("permalink", out var p) && p.GetString() is { } link)
                url = link;
        }
        catch { /* sin permalink */ }

        return (mediaId, url);
    }

    private async Task EsperarContenedorAsync(string containerId)
    {
        for (var i = 0; i < 20; i++)
        {
            var s = await GraphGetAsync(containerId, new() { ["fields"] = "status_code", ["access_token"] = _token! });
            var code = s.TryGetProperty("status_code", out var c) ? c.GetString() : null;
            if (code == "FINISHED") return;
            if (code is "ERROR" or "EXPIRED") throw new InvalidOperationException($"contenedor de Instagram en estado {code}");
            await Task.Delay(3000);
        }
        throw new TimeoutException("el contenedor de Instagram no quedó listo a tiempo");
    }

    // -------------------------------------------------- Graph API

    private async Task<JsonElement> GraphPostAsync(string path, Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync($"https://graph.facebook.com/{_ver}/{path}", content);
        return LeerRespuesta(await resp.Content.ReadAsStringAsync(), resp.StatusCode);
    }

    private async Task<JsonElement> GraphGetAsync(string path, Dictionary<string, string> query)
    {
        var qs = string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        using var resp = await _http.GetAsync($"https://graph.facebook.com/{_ver}/{path}?{qs}");
        return LeerRespuesta(await resp.Content.ReadAsStringAsync(), resp.StatusCode);
    }

    private static JsonElement LeerRespuesta(string body, HttpStatusCode status)
    {
        var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone();
        if ((int)status is >= 200 and < 300) return json;

        var msg = json.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
            ? m.GetString()
            : body;
        throw new InvalidOperationException($"Graph API {(int)status}: {msg}");
    }

    private static SocialPost Resultado(long id, SocialPost? previo, string estado,
        string? titulo = null, string? link = null, string? error = null) =>
        new(id, titulo ?? previo?.Titulo, link ?? previo?.Link, previo?.Imagenes ?? 0,
            previo?.FbPostId, previo?.FbUrl, previo?.IgId, previo?.IgUrl,
            estado, error, previo?.CreadoUtc ?? DateTime.UtcNow, DateTime.UtcNow);
}
