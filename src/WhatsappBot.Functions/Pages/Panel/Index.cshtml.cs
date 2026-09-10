using Microsoft.AspNetCore.Mvc.RazorPages;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Pages.Panel;

public class IndexModel : PageModel
{
    private readonly PanelData _data;
    public IndexModel(PanelData data) => _data = data;

    public PanelStats Stats { get; private set; } =
        new(0, 0, 0, 0, 0, 0, Array.Empty<FlujoConteo>());
    public IReadOnlyList<ConversacionResumen> Recientes { get; private set; } =
        Array.Empty<ConversacionResumen>();
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            Stats = await _data.StatsAsync();
            Recientes = (await _data.ConversacionesAsync(null)).Take(10).ToList();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
