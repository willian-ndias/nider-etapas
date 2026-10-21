using System;
using System.IO;
using System.Reflection;

namespace Trilha;

/// <summary>Nome, versão e caminhos do app. Trocar o nome do produto é só mudar aqui.</summary>
public static class AppInfo
{
    public const string Name = "Nider Etapas";
    /// <summary>Nome sem espaço: arquivo .exe, processo, registro.</summary>
    public const string Id = "NiderEtapas";
    public const string Tagline = "Manuais passo a passo, direto da sua tela";
    public const string Publisher = "Nider";
    public const string Author = "Willian Dias";
    public const string Email = "nider@nider.com.br";
    public const string Site = "nider.com.br";

    /// <summary>
    /// Endereço de um arquivo JSON com a última versão, por exemplo
    /// {"version":"2.1.0","url":"https://.../NiderEtapas.exe","notes":"..."}.
    /// Vazio = sem verificação automática pela internet.
    /// </summary>
    public const string UpdateUrl = "https://nider.com.br/api/apps/nider-etapas/versao";

    /// <summary>Manual em PDF, sempre o da versão mais nova (o site redireciona para o arquivo da release).</summary>
    public const string ManualUrl = "https://nider.com.br/api/apps/nider-etapas/manual";

    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    public static string ExeFileName => Id + ".exe";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);

    public static string InstalledExe => Path.Combine(InstallDir, ExeFileName);

    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeFileName);

    public static bool RunningFromInstallDir =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}

/// <summary>Grava erros em %LOCALAPPDATA%\Nider Etapas\erros.log para ajudar a corrigir problemas.</summary>
internal static class Log
{
    public static string PathFile => System.IO.Path.Combine(AppInfo.DataDir, "erros.log");

    public static void Error(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.AppendAllText(PathFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppInfo.VersionText} {where}: {ex}\r\n\r\n");
        }
        catch { }
    }
}
