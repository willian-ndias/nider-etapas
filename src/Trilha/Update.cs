using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Trilha;

/// <summary>
/// Atualização automática pelo site Nider, para qualquer usuário (sem login).
/// Ao abrir, consulta AppInfo.UpdateUrl. Havendo versão nova, o app pergunta, baixa o instalador
/// novo e o abre: ele mesmo mostra o painel "Atualizar" e troca a versão instalada.
/// </summary>
public static class Update
{
    /// <param name="Page">Página de downloads (plano B, aberta no navegador).</param>
    /// <param name="Download">Instalador (.exe) para baixar direto. Vazio = só a página.</param>
    public sealed record Info(Version Version, string Page, string Download, string Notes);

    public static async Task<Info?> CheckAsync()
    {
        if (string.IsNullOrWhiteSpace(AppInfo.UpdateUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(AppInfo.UpdateUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Resposta do site Nider: {"versao","data","notas","pagina","download"}.
            // Também aceita o formato antigo {"version","url","notes"}.
            string Text(params string[] keys)
            {
                foreach (var k in keys)
                    if (root.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String) return x.GetString() ?? "";
                return "";
            }
            var raw = Text("versao", "version").TrimStart('v', 'V');
            var dash = raw.IndexOf('-');
            if (dash > 0) raw = raw[..dash];
            if (!Version.TryParse(raw, out var version)) return null;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, AppInfo.Version.Build);
            if (version <= current) return null;

            var download = Text("download");
            if (string.IsNullOrWhiteSpace(download)) download = DirectDownload();
            if (!Trusted(download)) return null;
            return new Info(version, "", download, Text("notas", "notes"));
        }
        catch { return null; }
    }

    /// <summary>O instalador público do mesmo site. Nunca a página /downloads, que pede login.</summary>
    static string DirectDownload()
    {
        var url = AppInfo.UpdateUrl.TrimEnd('/');
        const string tail = "/versao";
        if (url.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            url = url[..^tail.Length] + "/baixar";
        return url;
    }

    /// <summary>Só baixa por https e do mesmo site de onde veio a consulta de versão.</summary>
    static bool Trusted(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        Uri.TryCreate(AppInfo.UpdateUrl, UriKind.Absolute, out var origin) &&
        string.Equals(u.Host, origin.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Baixa o instalador novo e devolve o caminho do .exe pronto para abrir. progress vai de 0 a 1.</summary>
    public static async Task<string> DownloadAsync(Info info, IProgress<double>? progress = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), AppInfo.Id + "-atualizacao");
        if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "download.part");

        if (string.IsNullOrWhiteSpace(info.Download))
            throw new InvalidOperationException("Não há link direto do instalador.");

        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) })
        {
            var url = info.Download;
            HttpResponseMessage? resp = null;
            try
            {
                for (var hop = 0; hop < 5; hop++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", AppInfo.Id);
                    resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    var code = (int)resp.StatusCode;
                    if (code is < 300 or >= 400) break;
                    var next = resp.Headers.Location ?? throw new InvalidDataException("O servidor não indicou o arquivo.");
                    var absolute = next.IsAbsoluteUri ? next : new Uri(new Uri(url), next);
                    if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("O instalador não veio por https.");
                    url = absolute.ToString();
                    resp.Dispose();
                    resp = null;
                }
                if (resp == null) throw new InvalidDataException("O instalador não foi encontrado.");
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            finally { resp?.Dispose(); }
        }

        var exe = Path.Combine(dir, $"{AppInfo.Id}-{info.Version}.exe");
        var head = new byte[2];
        await using (var f = File.OpenRead(tmp)) _ = await f.ReadAsync(head);

        // Só instalador do Windows (.exe começa com "MZ"); qualquer outra coisa é recusada.
        if (head[0] != 'M' || head[1] != 'Z')
            throw new InvalidDataException("O arquivo baixado não é um instalador do Windows.");
        File.Move(tmp, exe, true);
        progress?.Report(1);
        return exe;
    }
}
