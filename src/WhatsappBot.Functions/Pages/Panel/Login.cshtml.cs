using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WhatsappBot.Functions.Pages.Panel;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    private static readonly Dictionary<string, (int fallos, DateTime hasta)> Intentos = new();
    private static readonly object Lock = new();

    public LoginModel(IConfiguration config) => _config = config;

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/panel") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

        if (Bloqueado(ip))
        {
            Error = "Demasiados intentos fallidos. Espera unos minutos e inténtalo de nuevo.";
            return Page();
        }

        var email = (_config["Dashboard:Email"] ?? "").Trim();
        var password = _config["Dashboard:Password"] ?? "";

        var credencialesOk =
            !string.IsNullOrEmpty(email) &&
            !string.IsNullOrEmpty(password) &&
            FixedEquals(Email.Trim().ToLowerInvariant(), email.ToLowerInvariant()) &&
            FixedEquals(Password, password);

        if (!credencialesOk)
        {
            RegistrarFallo(ip);
            Error = "Correo o contraseña incorrectos.";
            return Page();
        }

        LimpiarFallos(ip);

        var identidad = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, email) },
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identidad));

        return Redirect("/panel");
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static bool Bloqueado(string ip)
    {
        lock (Lock)
            return Intentos.TryGetValue(ip, out var x) && x.fallos >= 6 && DateTime.UtcNow < x.hasta;
    }

    private static void RegistrarFallo(string ip)
    {
        lock (Lock)
        {
            var actual = Intentos.TryGetValue(ip, out var x) && DateTime.UtcNow < x.hasta ? x.fallos : 0;
            Intentos[ip] = (actual + 1, DateTime.UtcNow.AddMinutes(10));
        }
    }

    private static void LimpiarFallos(string ip)
    {
        lock (Lock) Intentos.Remove(ip);
    }
}
