using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WhatsappBot.Functions.Services;

namespace WhatsappBot.Functions.Pages.Panel;

public class ConversacionModel : PageModel
{
    private readonly PanelData _data;
    public ConversacionModel(PanelData data) => _data = data;

    [BindProperty(SupportsGet = true)] public string Tel { get; set; } = "";

    public ConversacionDetalle? Detalle { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Tel)) return Redirect("/panel/conversaciones");

        try
        {
            Detalle = await _data.ConversacionAsync(Tel.Trim());
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        return Page();
    }
}
