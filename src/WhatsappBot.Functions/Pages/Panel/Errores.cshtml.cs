using Microsoft.AspNetCore.Mvc.RazorPages;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Pages.Panel;

public class ErroresModel : PageModel
{
    private readonly PanelData _data;
    public ErroresModel(PanelData data) => _data = data;

    public IReadOnlyList<MensajeRow> Errores { get; private set; } = Array.Empty<MensajeRow>();
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        try { Errores = await _data.ErroresAsync(); }
        catch (Exception ex) { Error = ex.Message; }
    }
}
