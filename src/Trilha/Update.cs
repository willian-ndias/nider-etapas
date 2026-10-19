using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Trilha;

/// <summary>
/// Verificação de nova versão pela internet. Fica desligada enquanto AppInfo.UpdateUrl estiver vazio.
/// Quando houver versão nova, o app baixa o novo NiderEtapas.exe e o abre: ele mesmo mostra o painel de atualização.
/// </summary>
public static class Update
{
    public sealed record Info(Version Version, string Url, string Notes);

    public static async Task<Info?> CheckAsync()
    {
        if (string.IsNullOrWhiteSpace(AppInfo.UpdateUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(AppInfo.UpdateUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = Version.Parse(root.GetProperty("version").GetString() ?? "0.0.0");
            var url = root.GetProperty("url").GetString() ?? "";
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            return version > AppInfo.Version && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new Info(version, url, notes) : null;
        }
        catch { return null; }
    }

    public static async Task<string> DownloadAsync(Info info)
    {
        var dir = Path.Combine(Path.GetTempPath(), AppInfo.Id + "-atualizacao");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{AppInfo.Id}-{info.Version}.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await using (var src = await http.GetStreamAsync(info.Url))
        await using (var dst = File.Create(file))
            await src.CopyToAsync(dst);
        return file;
    }
}
