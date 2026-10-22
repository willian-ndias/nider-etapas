using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Trilha;

public enum SetupMode { Install, Update, AlreadyInstalled, NewerInstalled, Uninstall }

/// <summary>Painel próprio de instalação, atualização e desinstalação.</summary>
public partial class SetupWindow : Window
{
    readonly SetupMode _mode;
    readonly bool _automatic;
    bool _working, _finished;

    /// <summary>Verdadeiro quando a pessoa escolheu "Usar sem instalar".</summary>
    public bool RunPortable { get; private set; }

    public SetupWindow(SetupMode mode, bool automatic = false)
    {
        InitializeComponent();
        _mode = mode;
        _automatic = automatic;
        Shell.SizeChanged += (_, _) => Shell.Clip = new RectangleGeometry(new Rect(Shell.RenderSize), 28, 28);
        CreditText.Text = $"{AppInfo.Publisher}  ·  {AppInfo.Author}  ·  {AppInfo.Site}  ·  {AppInfo.Email}";
        SourceInitialized += (_, _) => _ = new Frost(this, Shell, FrostImage, Glass.ExcludeFromCapture(this));
        Glass.MakeDraggable(this, Shell);

        NameText.Text = AppInfo.Name;
        TaglineText.Text = AppInfo.Tagline;
        VersionText.Text = "v" + AppInfo.VersionText;
        var installed = Installer.InstalledVersion;
        var installedText = installed != null ? $"{installed.Major}.{installed.Minor}.{installed.Build}" : "";

        switch (mode)
        {
            case SetupMode.Install:
                Title = $"Instalar {AppInfo.Name}";
                PrimaryText.Text = "Instalar";
                break;
            case SetupMode.Update:
                Title = $"Atualizar {AppInfo.Name}";
                InstallMessage.Text = $"Você tem a versão {installedText}. Esta é a versão {AppInfo.VersionText}. Os seus atalhos continuam onde estão.";
                PrimaryText.Text = "Atualizar";
                DesktopSwitch.IsChecked = Installer.DesktopShortcutExists;
                PortableButton.Visibility = Visibility.Collapsed;
                break;
            case SetupMode.AlreadyInstalled:
                Title = AppInfo.Name;
                InstallMessage.Text = $"O {AppInfo.Name} já está instalado neste computador (versão {installedText}).";
                PrimaryText.Text = $"Abrir o {AppInfo.Name}";
                SecondaryAction.Content = "Reinstalar";
                SecondaryAction.Visibility = Visibility.Visible;
                DesktopSwitch.Visibility = LaunchSwitch.Visibility = Visibility.Collapsed;
                break;
            case SetupMode.NewerInstalled:
                Title = AppInfo.Name;
                InstallMessage.Text = $"Você já tem uma versão mais nova instalada ({installedText}).";
                PrimaryText.Text = $"Abrir o {AppInfo.Name}";
                SecondaryAction.Content = "Instalar esta mesmo assim";
                SecondaryAction.Visibility = Visibility.Visible;
                DesktopSwitch.Visibility = LaunchSwitch.Visibility = Visibility.Collapsed;
                break;
            case SetupMode.Uninstall:
                Title = $"Desinstalar {AppInfo.Name}";
                InstallMessage.Text = $"Quer mesmo remover o {AppInfo.Name}? Os manuais em PDF que você salvou não serão apagados.";
                PrimaryText.Text = "Desinstalar";
                SecondaryAction.Content = "Cancelar";
                SecondaryAction.Visibility = Visibility.Visible;
                DesktopSwitch.Visibility = LaunchSwitch.Visibility = PortableButton.Visibility = Visibility.Collapsed;
                break;
        }

        Loaded += async (_, _) =>
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            if (_automatic)
            {
                LaunchSwitch.IsChecked = true;
                await RunInstall();
            }
        };
    }

    async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        switch (_mode)
        {
            case SetupMode.AlreadyInstalled:
            case SetupMode.NewerInstalled:
                Installer.LaunchInstalled();
                Close();
                return;
            case SetupMode.Uninstall:
                await RunUninstall();
                return;
            default:
                await RunInstall();
                return;
        }
    }

    async void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        if (_mode == SetupMode.Uninstall) { Close(); return; }
        // Reinstalar / instalar esta mesmo assim
        DesktopSwitch.Visibility = LaunchSwitch.Visibility = Visibility.Visible;
        await RunInstall();
    }

    async System.Threading.Tasks.Task RunInstall()
    {
        _working = true;
        CloseButton.IsEnabled = false;
        InstallPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        var desktop = DesktopSwitch.IsChecked == true;
        var launch = LaunchSwitch.IsChecked == true;
        var progress = new Progress<(double value, string text)>(p =>
        {
            Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(p.value, TimeSpan.FromMilliseconds(180)));
            ProgressText.Text = p.text;
        });
        try
        {
            await Installer.InstallAsync(desktop, progress);
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            _working = false;
            CloseButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallPanel.Visibility = Visibility.Visible;
            GlassDialog.Error(this, "Não consegui instalar: " + ex.Message);
            return;
        }
        await System.Threading.Tasks.Task.Delay(350);
        _working = false;
        _finished = true;
        CloseButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DoneTitle.Text = _mode == SetupMode.Update ? "Atualizado!" : "Tudo pronto!";
        DoneMessage.Text = $"O {AppInfo.Name} está no menu Iniciar" + (desktop ? " e na área de trabalho" : "") +
                           ". Para gravar, é só clicar no botão vermelho da barrinha.";
        DoneButtonText.Text = $"Abrir o {AppInfo.Name}";
        if (launch)
        {
            Installer.LaunchInstalled();
            await System.Threading.Tasks.Task.Delay(900);
            Close();
        }
    }

    async System.Threading.Tasks.Task RunUninstall()
    {
        _working = true;
        InstallPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Removendo...";
        Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)));
        await System.Threading.Tasks.Task.Run(Installer.Uninstall);
        await System.Threading.Tasks.Task.Delay(700);
        _working = false;
        _finished = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DoneTitle.Text = $"{AppInfo.Name} removido";
        DoneMessage.Text = "Obrigado por usar! Os seus manuais em PDF continuam onde você salvou.";
        DoneButtonText.Text = "Fechar";
    }

    void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != SetupMode.Uninstall) Installer.LaunchInstalled();
        Close();
    }

    void Portable_Click(object sender, RoutedEventArgs e)
    {
        RunPortable = true;
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_working) Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_working) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    public bool Finished => _finished;
}
