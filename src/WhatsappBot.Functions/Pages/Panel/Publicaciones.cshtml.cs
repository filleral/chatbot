using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Pages.Panel;

public class PublicacionesModel : PageModel
{
    private readonly SocialPublisher _pub;
    public PublicacionesModel(SocialPublisher pub) => _pub = pub;

    public IReadOnlyList<SocialPost> Posts { get; private set; } = Array.Empty<SocialPost>();
    public bool Configurado { get; private set; }
    public string? Error { get; private set; }

    [TempData] public string? Aviso { get; set; }

    public async Task OnGetAsync()
    {
        Configurado = _pub.Configurado;
        try { Posts = await _pub.ListarAsync(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    public async Task<IActionResult> OnPostPublicarAsync(string entrada)
    {
        try
        {
            var r = await _pub.PublicarPorEntradaAsync(entrada);
            Aviso = $"Inmueble {r.PostId} — {r.Estado}" + (r.Error is null ? " ✅" : $" · {r.Error}");
        }
        catch (Exception ex)
        {
            Aviso = $"No se pudo publicar: {ex.Message}";
        }
        return RedirectToPage();
    }
}
