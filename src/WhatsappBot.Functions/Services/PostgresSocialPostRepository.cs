using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace WhatsappBot.Functions.Services;

public class PostgresSocialPostRepository : ISocialPostRepository
{
    private readonly string _cs;

    public PostgresSocialPostRepository(IConfiguration config)
    {
        _cs = config["PostgresConnectionString"]
            ?? throw new InvalidOperationException("Falta PostgresConnectionString en la configuración.");
    }

    public async Task<SocialPost?> ObtenerAsync(long postId)
    {
        await using var conn = new NpgsqlConnection(_cs);
        return await conn.QuerySingleOrDefaultAsync<SocialPost>(@"
            SELECT post_id AS PostId, titulo AS Titulo, link AS Link, imagenes AS Imagenes,
                   fb_post_id AS FbPostId, fb_url AS FbUrl, ig_id AS IgId, ig_url AS IgUrl,
                   estado AS Estado, error AS Error, created_at_utc AS CreadoUtc, updated_at_utc AS ActualizadoUtc
            FROM social_posts WHERE post_id = @postId;", new { postId });
    }

    public async Task GuardarAsync(SocialPost p)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.ExecuteAsync(@"
            INSERT INTO social_posts
                (post_id, titulo, link, imagenes, fb_post_id, fb_url, ig_id, ig_url, estado, error, updated_at_utc)
            VALUES
                (@PostId, @Titulo, @Link, @Imagenes, @FbPostId, @FbUrl, @IgId, @IgUrl, @Estado, @Error, now())
            ON CONFLICT (post_id) DO UPDATE SET
                titulo = EXCLUDED.titulo, link = EXCLUDED.link, imagenes = EXCLUDED.imagenes,
                fb_post_id = COALESCE(EXCLUDED.fb_post_id, social_posts.fb_post_id),
                fb_url     = COALESCE(EXCLUDED.fb_url, social_posts.fb_url),
                ig_id      = COALESCE(EXCLUDED.ig_id, social_posts.ig_id),
                ig_url     = COALESCE(EXCLUDED.ig_url, social_posts.ig_url),
                estado = EXCLUDED.estado, error = EXCLUDED.error, updated_at_utc = now();",
            new
            {
                p.PostId, p.Titulo, p.Link, p.Imagenes,
                p.FbPostId, p.FbUrl, p.IgId, p.IgUrl, p.Estado, p.Error
            });
    }

    public async Task<IReadOnlyList<SocialPost>> ListarAsync(int limite = 200)
    {
        await using var conn = new NpgsqlConnection(_cs);
        return (await conn.QueryAsync<SocialPost>(@"
            SELECT post_id AS PostId, titulo AS Titulo, link AS Link, imagenes AS Imagenes,
                   fb_post_id AS FbPostId, fb_url AS FbUrl, ig_id AS IgId, ig_url AS IgUrl,
                   estado AS Estado, error AS Error, created_at_utc AS CreadoUtc, updated_at_utc AS ActualizadoUtc
            FROM social_posts ORDER BY updated_at_utc DESC LIMIT @limite;", new { limite })).ToList();
    }
}
