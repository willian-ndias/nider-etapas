using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Trilha;

/// <summary>
/// O próprio NiderEtapas.exe é o instalador: ele se copia para a pasta do usuário,
/// cria atalhos e se registra em "Aplicativos" do Windows. Não precisa de administrador.
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Id;
    const string OldEtapasKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Etapas";

    static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
    static string StartMenuLink => Path.Combine(StartMenuDir, AppInfo.Name + ".lnk");
    static string PinnedLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar", AppInfo.Name + ".lnk");

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Avisa o Windows que os ícones mudaram (senão a barra de tarefas e o menu Iniciar ficam com o ícone antigo em cache).</summary>
    public static void RefreshShellIcons()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { Log.Error("RefreshShellIcons", ex); }
    }

    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk");

    public static bool IsInstalled => File.Exists(AppInfo.InstalledExe);

    public static Version? InstalledVersion
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                if (k?.GetValue("DisplayVersion") is string v && Version.TryParse(v, out var ver)) return ver;
                if (IsInstalled)
                {
                    var fv = FileVersionInfo.GetVersionInfo(AppInfo.InstalledExe);
                    return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
                }
            }
            catch { }
            return null;
        }
    }

    public static bool DesktopShortcutExists => File.Exists(DesktopLink);

    /// <summary>Instala ou atualiza. progress vai de 0 a 1.</summary>
    public static async Task InstallAsync(bool desktopShortcut, IProgress<(double value, string text)> progress)
    {
        progress.Report((0.02, "Preparando..."));
        await Task.Run(() => CloseRunningInstalled());

        progress.Report((0.06, "Removendo a versão antiga do Etapas..."));
        await Task.Run(RemoveOldEtapas);

        Directory.CreateDirectory(AppInfo.InstallDir);
        var tmp = AppInfo.InstalledExe + ".novo";
        progress.Report((0.1, "Copiando arquivos..."));
        await using (var src = new FileStream(AppInfo.CurrentExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1 << 20];
            long total = src.Length, done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                progress.Report((0.1 + 0.75 * done / total, "Copiando arquivos..."));
            }
        }
        await Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                try { File.Move(tmp, AppInfo.InstalledExe, overwrite: true); return; }
                catch (IOException) { Thread.Sleep(250); }
            }
            File.Move(tmp, AppInfo.InstalledExe, overwrite: true);
        });

        progress.Report((0.9, "Criando atalhos..."));
        Directory.CreateDirectory(StartMenuDir);
        Shortcut.Create(StartMenuLink, AppInfo.InstalledExe, AppInfo.Tagline);
        if (desktopShortcut) Shortcut.Create(DesktopLink, AppInfo.InstalledExe, AppInfo.Tagline);
        else TryDelete(DesktopLink);
        // Atalho fixado na barra de tarefas: recria para o Windows pegar o ícone novo.
        if (File.Exists(PinnedLink)) Shortcut.Create(PinnedLink, AppInfo.InstalledExe, AppInfo.Tagline);
        RefreshShellIcons();

        progress.Report((0.96, "Registrando no Windows..."));
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", AppInfo.Name);
            k.SetValue("DisplayVersion", AppInfo.VersionText);
            k.SetValue("Publisher", AppInfo.Publisher);
            k.SetValue("Contact", $"{AppInfo.Author} · {AppInfo.Email}");
            k.SetValue("URLInfoAbout", "https://" + AppInfo.Site);
            k.SetValue("HelpLink", "https://" + AppInfo.Site);
            k.SetValue("DisplayIcon", AppInfo.InstalledExe + ",0");
            k.SetValue("InstallLocation", AppInfo.InstallDir);
            k.SetValue("UninstallString", $"\"{AppInfo.InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{AppInfo.InstalledExe}\" --uninstall --quiet");
            k.SetValue("EstimatedSize", (int)(new FileInfo(AppInfo.InstalledExe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        progress.Report((1, "Pronto!"));
    }

    /// <summary>Remove atalhos e registro, e apaga a pasta depois que o app fechar.</summary>
    public static void Uninstall()
    {
        CloseRunningInstalled(exceptSelf: true);
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        // O exe em uso não pode se apagar sozinho: um cmd escondido espera ele fechar e apaga a pasta.
        var dir = AppInfo.InstallDir;
        // Tenta por até ~1 minuto: a pasta só some depois que a janela "removido" for fechada.
        var cmd = $"/c for /l %i in (1,1,30) do (ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\" 2>nul & if not exist \"{dir}\" exit)";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch { }
    }

    public static void LaunchInstalled()
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.InstalledExe) { UseShellExecute = true, WorkingDirectory = AppInfo.InstallDir }); }
        catch (Exception ex) { Log.Error("Launch", ex); }
    }

    static void CloseRunningInstalled(bool exceptSelf = false)
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(AppInfo.Id))
        {
            try
            {
                if (p.Id == self) continue;
                var path = p.MainModule?.FileName;
                if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(AppInfo.InstalledExe), StringComparison.OrdinalIgnoreCase)) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Se o "Etapas" antigo (instalador anterior) estiver instalado, desinstala em silêncio.</summary>
    static void RemoveOldEtapas()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(OldEtapasKey);
            if (k?.GetValue("UninstallString") is not string un) return;
            var exe = un.Trim('"');
            if (!File.Exists(exe)) return;
            using var p = Process.Start(new ProcessStartInfo(exe, "/S") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15000);
        }
        catch { }
    }

    static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}

/// <summary>Cria atalhos .lnk usando a interface do próprio Windows.</summary>
internal static class Shortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public static void Create(string linkPath, string target, string description)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(linkPath, false);
            Marshal.ReleaseComObject(link);
        }
        catch (Exception ex) { Log.Error("Shortcut", ex); }
    }
}
