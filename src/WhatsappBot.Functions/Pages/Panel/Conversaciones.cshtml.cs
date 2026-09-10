using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Pages.Panel;

public class ConversacionesModel : PageModel
{
    private readonly PanelData _data;
    public ConversacionesModel(PanelData data) => _data = data;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public IReadOnlyList<ConversacionResumen> Conversaciones { get; private set; } =
        Array.Empty<ConversacionResumen>();
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            Conversaciones = await _data.ConversacionesAsync(Q);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
