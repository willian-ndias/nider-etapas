using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Trilha;

public partial class App : Application
{
    static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnError;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => { if (ev.ExceptionObject is Exception ex) Log.Error("Fatal", ex); };

        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();

        if (args.Contains("--uninstall"))
        {
            if (args.Contains("--quiet")) { Installer.Uninstall(); Shutdown(); return; }
            var u = new SetupWindow(SetupMode.Uninstall);
            u.Closed += (_, _) => Shutdown();
            u.Show();
            return;
        }

        // Aberto fora da pasta de instalação (ex.: Downloads): mostra o painel de instalação/atualização.
        if (!AppInfo.RunningFromInstallDir && !args.Contains("--portable"))
        {
            var installed = Installer.InstalledVersion;
            var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, AppInfo.Version.Build);
            var mode = installed == null ? SetupMode.Install
                     : installed < current ? SetupMode.Update
                     : installed == current ? SetupMode.AlreadyInstalled
                     : SetupMode.NewerInstalled;
            var s = new SetupWindow(mode);
            s.Closed += (_, _) => { if (s.RunPortable) StartMain(); else Shutdown(); };
            s.Show();
            return;
        }

        StartMain();
    }

    void StartMain()
    {
        _single = new Mutex(true, @"Local\" + AppInfo.Id + ".GravadorDePassos", out var first);
        if (!first)
        {
            GlassDialog.Show(null, $"O {AppInfo.Name} já está aberto", "Procure a barrinha de vidro no topo da tela.", "OK");
            Shutdown();
            return;
        }
        // Tela de abertura rápida; a barrinha aparece quando ela some.
        var splash = new SplashWindow();
        splash.Closed += (_, _) => new ToolbarWindow().Show();
        splash.Show();
    }

    void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        e.Handled = true;
        try { GlassDialog.Error(null, e.Exception.Message); } catch { }
    }
}
