using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Trilha;

/// <summary>A barra flutuante de vidro: gravar, pausar, desenhar e parar.</summary>
public partial class ToolbarWindow : Window
{
    const string DrawHotkey = "Ctrl+Shift+D";
    const string PauseHotkey = "Ctrl+Shift+P";
    const int WM_HOTKEY = 0x0312, HK_DRAW = 1, HK_PAUSE = 2;
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly List<Step> _steps = new();
    Recorder? _recorder;
    bool _excluded, _dirty, _busy, _pausedByLive;
    LivePalette? _live;

    public bool ExcludedFromCapture => _excluded;
    TimeSpan _elapsed;
    DateTime _runningSince;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Storyboard? _pulse;
    Update.Info? _update;

    public ToolbarWindow()
    {
        InitializeComponent();
        Title = AppInfo.Name;
        Shell.SizeChanged += (_, _) => Shell.Clip = new RectangleGeometry(new Rect(Shell.RenderSize), 30, 30);
        Glass.MakeDraggable(this, Shell);
        _clock.Tick += (_, _) => UpdateTimer();

        SourceInitialized += (_, _) =>
        {
            var h = Glass.Handle(this);
            _excluded = Glass.ExcludeFromCapture(this);
            _ = new Frost(this, Shell, FrostImage, _excluded);
            HwndSource.FromHwnd(h)?.AddHook(WndProc);
            RegisterHotKey(h, HK_DRAW, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)WF.Keys.D);
            RegisterHotKey(h, HK_PAUSE, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)WF.Keys.P);
        };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 6;
            ArmUpdateWatch();
        };
        UpdateUi();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            if ((int)wParam == HK_DRAW) DrawOnScreen();
            else if ((int)wParam == HK_PAUSE && _recorder != null) TogglePause();
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------ estado visual

    void UpdateUi()
    {
        var recording = _recorder != null;
        var paused = _recorder?.Paused == true;

        if (!recording)
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = (Brush)FindResource("RecordBrush") };
            dot.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.8, Color = (Color)FindResource("RecordColor") };
            RecordButton.Content = dot;
            RecordButton.Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0x4D, 0x6A));
            RecordTip.Content = "Gravar";
        }
        else
        {
            var p = new Path { Data = (Geometry)FindResource(paused ? "IcPlay" : "IcPause"), Style = (Style)FindResource("Icon") };
            if (paused) p.Fill = Brushes.White;
            RecordButton.Content = p;
            RecordButton.Background = paused ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
                                             : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x4D, 0x6A));
            RecordTip.Content = paused ? $"Continuar gravando ({PauseHotkey})" : $"Pausar ({PauseHotkey})";
        }

        StopButton.IsEnabled = recording || _steps.Count > 0;
        var real = System.Linq.Enumerable.Count(_steps, s => !s.IsTextBlock);
        var count = real == 1 ? "1 passo" : $"{real} passos";
        StatusText.Text = _live != null ? $"Anotando · {count}"
                        : !recording ? (_steps.Count == 0 ? "Pronto para gravar" : count)
                        : paused ? $"Pausado · {count}" : $"Gravando · {count}";

        LiveDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording && !paused) StartPulse(); else StopPulse();
        if (paused) LiveDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x4D));
        else LiveDot.Fill = (Brush)FindResource("RecordBrush");
        UpdateTimer();
    }

    void StartPulse()
    {
        if (_pulse != null) return;
        var a = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, LiveDot);
        Storyboard.SetTargetProperty(a, new PropertyPath(OpacityProperty));
        _pulse = new Storyboard();
        _pulse.Children.Add(a);
        _pulse.Begin(this, true);
    }

    void StopPulse()
    {
        _pulse?.Stop(this);
        _pulse = null;
        LiveDot.Opacity = 1;
    }

    void UpdateTimer()
    {
        var t = _elapsed + (_recorder != null && !_recorder.Paused ? DateTime.Now - _runningSince : TimeSpan.Zero);
        TimerText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    void Bump()
    {
        var a = new DoubleAnimation(1.12, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() };
        StatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        StatusScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ------------------------------------------------------------ ações

    void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder == null) StartRecording(); else TogglePause();
    }

    void StartRecording()
    {
        _recorder = new Recorder();
        _recorder.IgnoredCombos.Add(DrawHotkey);
        _recorder.IgnoredCombos.Add(PauseHotkey);
        _recorder.StepCaptured += s => Dispatcher.BeginInvoke(() => { _steps.Add(s); _dirty = true; UpdateUi(); Bump(); });
        _recorder.Start();
        _runningSince = DateTime.Now;
        _clock.Start();
        UpdateUi();
    }

    void TogglePause()
    {
        if (_recorder == null) return;
        if (_recorder.Paused) _runningSince = DateTime.Now;
        else _elapsed += DateTime.Now - _runningSince;
        _recorder.Paused = !_recorder.Paused;
        UpdateUi();
    }

    void StopRecording()
    {
        _live?.Close();
        if (_recorder != null)
        {
            if (!_recorder.Paused) _elapsed += DateTime.Now - _runningSince;
            Cursor = System.Windows.Input.Cursors.Wait;
            _recorder.Stop();
            _recorder = null;
            _clock.Stop();
            Cursor = null;
        }
        UpdateUi();
        // Deixa os últimos passos (que ainda estão chegando) entrarem antes de abrir a revisão.
        Dispatcher.BeginInvoke(OpenReview, DispatcherPriority.Background);
    }

    void Stop_Click(object sender, RoutedEventArgs e) => StopRecording();

    void OpenReview()
    {
        if (_steps.Count == 0)
        {
            GlassDialog.Show(this, "Nenhum passo ainda",
                "Clique no botão vermelho e use o computador normalmente. Cada clique vira um passo com print.", "Entendi");
            return;
        }
        if (_busy) return;
        _busy = true;
        Hide();
        try
        {
            var review = new ReviewWindow(_steps);
            review.ShowDialog();
            if (review.Saved) _dirty = false;
            if (review.StartNew) { _steps.Clear(); _dirty = false; _elapsed = TimeSpan.Zero; }
        }
        finally
        {
            _busy = false;
            UpdateUi();
            Show();
            Activate();
        }
    }

    void Draw_Click(object sender, RoutedEventArgs e) => DrawOnScreen();

    /// <summary>Abre a paleta com a tela viva. Se ela já está aberta, o atalho tira o print.</summary>
    void DrawOnScreen()
    {
        if (_live != null) { _live.TakeShot(); return; }
        if (_busy || !IsVisible) return;
        try
        {
            var monitor = WF.Screen.FromPoint(WF.Cursor.Position).Bounds;
            // Com a paleta aberta, nenhum clique vira print: a gravação pausa e volta sozinha ao fechar.
            _pausedByLive = _recorder != null && !_recorder.Paused;
            if (_pausedByLive) TogglePause();
            var live = new LivePalette(monitor, this);
            live.StepTaken += s => { _steps.Add(s); _dirty = true; UpdateUi(); Bump(); };
            live.Closed += (_, _) =>
            {
                _live = null;
                if (_pausedByLive && _recorder?.Paused == true) TogglePause();
                _pausedByLive = false;
                UpdateUi();
            };
            _live = live;
            live.Show();
            UpdateUi();
        }
        catch (Exception ex)
        {
            Log.Error("Paleta", ex);
            _live = null;
            if (_pausedByLive && _recorder?.Paused == true) TogglePause();
            _pausedByLive = false;
            GlassDialog.Error(this, ex.Message);
        }
    }

    void Marker_Click(object sender, RoutedEventArgs e)
    {
        var w = new MarkerWindow(ClickStyle.Current) { Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (w.ShowDialog() == true) ClickStyle.Current = w.Picked;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && _steps.Count > 0)
        {
            var r = GlassDialog.Show(this, "Salvar antes de sair?",
                $"Você tem {_steps.Count} passo(s) que ainda não viraram PDF.", "Revisar e salvar", "Sair sem salvar", "Cancelar");
            if (r == 0) { e.Cancel = true; StopRecording(); return; }
            if (r != 1) { e.Cancel = true; return; }
        }
        _updateWatch?.Stop();
        _live?.Close();
        _recorder?.Stop();
        _recorder = null;
        var h = Glass.Handle(this);
        UnregisterHotKey(h, HK_DRAW);
        UnregisterHotKey(h, HK_PAUSE);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    void Manual_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.ManualUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Manual", ex); GlassDialog.Error(this, "Não consegui abrir o manual."); }
    }

    void Info_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (_update != null) AskUpdate();
        else About.Show(this);
    }

    // ------------------------------------------------------------ atualização

    DispatcherTimer? _updateWatch;
    bool _updateShown;

    /// <summary>Com o programa aberto, olha de novo a cada 4 horas. Versão nova troca o ícone de informação. Não baixa.</summary>
    void ArmUpdateWatch()
    {
        _updateWatch = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _updateWatch.Tick += (_, _) => _ = LookForUpdateAsync();
        _updateWatch.Start();
        _ = LookForUpdateAsync();
    }

    async Task LookForUpdateAsync()
    {
        if (_updating) return;
        var found = await Update.CheckAsync();
        if (found == null || _updating) return;
        if (_updateShown && _update != null && found.Version == _update.Version) return;
        _update = found;
        ShowUpdateReady();
    }

    void ShowUpdateReady()
    {
        _updateShown = true;
        UpdateArrowShift.BeginAnimation(TranslateTransform.YProperty, null);
        UpdateArrowShift.Y = 0;
        InfoIcon.Data = (System.Windows.Media.Geometry)FindResource("IcUpdate");
        InfoIcon.Width = 20;
        InfoIcon.Height = 20;
        InfoIcon.Opacity = 1;
        UpdateLabel.Visibility = Visibility.Visible;
        UpdatePercent.Visibility = Visibility.Collapsed;
        UpdatePercent.Text = "";
        InfoButton.ClearValue(BackgroundProperty);
        InfoButton.ClearValue(ForegroundProperty);
        InfoButton.ToolTip = $"Nova versão {_update!.Version}. Clique para atualizar.";
    }

    void AskUpdate()
    {
        if (_update == null || _updating) return;
        if (IsRecordingOrBusy())
        {
            GlassDialog.Show(this, $"Nova versão {_update.Version}",
                "Termine a gravação e salve o PDF antes de atualizar. Depois é só clicar de novo no ícone de atualização.", "OK");
            return;
        }
        var notes = string.IsNullOrWhiteSpace(_update.Notes) ? "" : "\n\n" + _update.Notes.Trim();
        var choice = GlassDialog.Show(this, $"Nova versão {_update.Version}",
            $"Há uma atualização do Nider Etapas.{notes}", "Baixar", "Depois");
        if (choice == 0) _ = OfferUpdateAsync();
    }

    /// <summary>Só depois do Baixar: o ícone desce e a porcentagem aparece.</summary>
    void StartUpdateArrow()
    {
        InfoIcon.Data = (System.Windows.Media.Geometry)FindResource("IcDownload");
        InfoIcon.Width = 20;
        InfoIcon.Height = 20;
        InfoIcon.Opacity = 1;
        UpdateLabel.Visibility = Visibility.Collapsed;
        UpdatePercent.Visibility = Visibility.Visible;
        UpdatePercent.Text = "0%";
        InfoButton.ClearValue(BackgroundProperty);
        InfoButton.ClearValue(ForegroundProperty);
        var move = new DoubleAnimation(-4, 5, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        UpdateArrowShift.BeginAnimation(TranslateTransform.YProperty, move);
    }

    void ShowDownloadStatus(double p)
    {
        var pct = $"{Math.Clamp(p, 0, 1):P0}";
        UpdatePercent.Text = pct;
        UpdatePercent.Visibility = Visibility.Visible;
        InfoButton.ToolTip = $"Baixando a versão {_update!.Version} · {pct}";
    }

    void StopUpdateArrow()
    {
        UpdateArrowShift.BeginAnimation(TranslateTransform.YProperty, null);
        UpdateArrowShift.Y = 0;
        InfoIcon.Opacity = 1;
        UpdatePercent.Visibility = Visibility.Collapsed;
        UpdatePercent.Text = "";
        if (_update != null) ShowUpdateReady();
    }

    bool _updating;

    /// <summary>Gravando ou com passos ainda não salvos: atualizar agora fecharia o app e perderia o trabalho.</summary>
    bool IsRecordingOrBusy() => _recorder != null || (_dirty && _steps.Count > 0);

    async Task OfferUpdateAsync()
    {
        if (_update == null || _updating) return;
        if (IsRecordingOrBusy())
        {
            GlassDialog.Show(this, $"Nova versão {_update.Version} disponível",
                "Termine a gravação e salve o PDF antes de atualizar. Depois é só clicar de novo no ícone de download.", "OK");
            return;
        }
        _updating = true;
        var tip = InfoButton.ToolTip;
        StartUpdateArrow();
        ShowDownloadStatus(0);
        try
        {
            var progress = new Progress<double>(ShowDownloadStatus);
            var exe = await Update.DownloadAsync(_update, progress);
            // /atualizar instala e reabre sozinho. Não abre o site (a página de downloads pede login).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = "/atualizar",
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Update", ex);
            _updating = false;
            StopUpdateArrow();
            InfoButton.ToolTip = tip;
            GlassDialog.Show(this, "Não deu para baixar a atualização",
                "Verifique a internet e clique de novo no ícone de atualização.", "OK");
        }
    }
}
