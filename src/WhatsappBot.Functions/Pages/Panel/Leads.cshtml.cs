using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Pages.Panel;

public class LeadsModel : PageModel
{
    private readonly PanelData _data;
    public LeadsModel(PanelData data) => _data = data;

    [BindProperty(SupportsGet = true)] public string? Flujo { get; set; }
    [BindProperty(SupportsGet = true)] public string? Estado { get; set; }

    public IReadOnlyList<LeadRow> Leads { get; private set; } = Array.Empty<LeadRow>();
    public string? Error { get; private set; }

    public static readonly (string val, string txt)[] Flujos =
    {
        ("", "Todos los flujos"),
        ("arriendo", "Arriendo"), ("administracion", "Administración"), ("compra", "Compra"),
        ("venta", "Venta"), ("asesoria_venta", "Asesoría venta"),
        ("notarial", "Notarial"), ("juridico", "Jurídico"), ("otro", "Otro"),
    };

    public async Task OnGetAsync()
    {
        try { Leads = await _data.LeadsAsync(Flujo, Estado); }
        catch (Exception ex) { Error = ex.Message; }
    }

    public async Task<IActionResult> OnPostEstadoAsync(long id, string estado)
    {
        try { await _data.MarcarLeadAsync(id, estado); }
        catch { /* se refleja al recargar */ }
        return RedirectToPage(new { Flujo, Estado });
    }
}
