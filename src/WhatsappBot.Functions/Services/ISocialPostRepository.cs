namespace WhatsappBot.Functions.Services;

/// <summary>Un inmueble publicado (o intentado) en redes sociales.</summary>
public record SocialPost(
    long PostId,
    string? Titulo,
    string? Link,
    int Imagenes,
    string? FbPostId,
    string? FbUrl,
    string? IgId,
    string? IgUrl,
    string Estado,          // publicado | parcial | error | omitido
    string? Error,
    DateTime CreadoUtc,
    DateTime ActualizadoUtc);

public interface ISocialPostRepository
{
    Task<SocialPost?> ObtenerAsync(long postId);
    Task GuardarAsync(SocialPost post);
    Task<IReadOnlyList<SocialPost>> ListarAsync(int limite = 200);
}
