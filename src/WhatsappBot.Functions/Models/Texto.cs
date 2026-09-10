using System.Globalization;
using System.Text;

namespace WhatsappBot.Functions.Models;

public static class Texto
{
    /// <summary>Minúsculas, sin tildes ni marcas diacríticas, recortado. Para comparar texto libre.</summary>
    public static string Normalizar(string? s)
    {
        s = (s ?? "").ToLowerInvariant().Trim();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
