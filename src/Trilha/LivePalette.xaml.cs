using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace Trilha;

/// <summary>
/// Paleta de desenho com a tela VIVA: dá para continuar usando o computador com os desenhos por cima.
/// Enquanto está aberta, nenhum clique vira print; o print só sai no botão "Tirar print" (ou Ctrl+Shift+D).
/// Cada print vira um passo e os desenhos continuam na tela até você apagar.
/// </summary>
public partial class LivePalette : Window
{
    readonly LiveLayer _layer;
    readonly SD.Rectangle _monitor;
    readonly Window _toolbar;
    bool _excluded;
    SD.Bitmap? _frozen;
    int _shots;
    readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public event Action<Step>? StepTaken;

    public LivePalette(SD.Rectangle monitor, Window toolbar)
    {
        InitializeComponent();
        Title = AppInfo.Name;
        _monitor = monitor;
        _toolbar = toolbar;
        _layer = new LiveLayer(monitor);
        _layer.Show();
        Owner = _layer;                       // a paleta fica sempre acima da camada de desenho
        Palette.Attach(_layer.Surface);
        Shell.SizeChanged += (_, _) => Shell.Clip = new RectangleGeometry(new Rect(Shell.RenderSize), 22, 22);
        Glass.MakeDraggable(this, Shell);
        // Escolher uma ferramenta já volta para o modo desenhar.
        Palette.Tools.PreviewMouseLeftButtonDown += (_, _) => ModeDraw.IsChecked = true;
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); ShowHint(); };

        SourceInitialized += (_, _) =>
        {
            _excluded = Glass.ExcludeFromCapture(this);
            _ = new Frost(this, Shell, FrostImage, _excluded);
        };
        Loaded += (_, _) => PlaceAtBottom();
        PreviewKeyDown += OnKey;
        _layer.PreviewKeyDown += OnKey;
        ModeDraw.IsChecked = true;
        ShowHint();
    }

    public int Shots => _shots;

    void PlaceAtBottom()
    {
        try
        {
            var wa = System.Windows.Forms.Screen.FromRectangle(_monitor).WorkingArea;
            var me = Glass.PhysicalBounds(this);
            Glass.PlacePhysical(this, new SD.Rectangle(wa.X + (wa.Width - me.Width) / 2, wa.Bottom - me.Height - 8, me.Width, me.Height), topmost: true);
        }
        catch { }
    }

    // ------------------------------------------------------------ modo

    bool Drawing => ModeDraw.IsChecked == true;

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_layer == null) return;
        if (!Drawing) _layer.Surface.CommitText();
        _layer.SetDrawing(Drawing || _frozen != null);
        ShowHint();
    }

    void ShowHint()
    {
        if (_hintTimer.IsEnabled) return;
        Hint.Text = _frozen != null
            ? "Tela congelada: desenhe e tire o print. \"Descongelar\" volta para a tela viva."
            : Drawing
                ? "Desenhando por cima da tela. Para clicar no programa, use \"Mexer na tela\". A gravação está pausada."
                : "Use o computador normalmente: os desenhos ficam por cima e nenhum clique vira print. Menu que some? Abra e aperte Ctrl+Shift+D.";
    }

    void Flash(string text)
    {
        _hintTimer.Stop();
        Hint.Text = text;
        _hintTimer.Start();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox || _layer.Surface.IsEditingText;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!typing && ctrl && e.Key == Key.Z) { _layer.Surface.Undo(); e.Handled = true; }
        else if (!typing && ctrl && e.Key == Key.Y) { _layer.Surface.Redo(); e.Handled = true; }
        else if (e.Key == Key.Escape && !_layer.Surface.IsEditingText) { Close(); e.Handled = true; }
    }

    // ------------------------------------------------------------ print

    /// <summary>Print da tela sem a paleta, a camada e a barra (elas ficam fora do print, ou somem por um instante).</summary>
    SD.Bitmap CaptureClean()
    {
        var toolbarExcluded = _toolbar is ToolbarWindow t && t.ExcludedFromCapture;
        if (_excluded && _layer.Excluded && toolbarExcluded) return Imaging.Capture(_monitor);
        // Windows antigo: some com tudo por um instante, sem tirar o foco do programa (um menu aberto continua aberto).
        double a = _layer.Opacity, c = _toolbar.Opacity;
        _layer.Opacity = 0; _toolbar.Opacity = 0;
        Glass.ShowNoActivate(this, false);
        try
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(200);
            return Imaging.Capture(_monitor);
        }
        finally { Glass.ShowNoActivate(this, true); _layer.Opacity = a; _toolbar.Opacity = c; }
    }

    public void TakeShot()
    {
        try
        {
            _layer.Surface.CommitText();
            using var shot = _frozen != null ? new SD.Bitmap(_frozen) : CaptureClean();
            var step = Step.FromBitmap(shot);
            step.Marks.AddRange(_layer.Surface.Marks.Select(m => m.Clone()));
            var c = Comment.Text.Trim();
            step.Description = c.Length > 0 ? c : _layer.Surface.Marks.Count > 0 ? "Observe os destaques na imagem." : "Observe a tela.";
            step.Time = DateTime.Now;
            step.TrimTaskbar(_monitor);
            StepTaken?.Invoke(step);
            _shots++;
            Comment.Text = "";
            Flash(_shots == 1 ? "Print adicionado ao manual. Os desenhos continuam na tela para o próximo."
                              : $"{_shots} prints adicionados nesta paleta.");
        }
        catch (Exception ex) { Log.Error("Print ao vivo", ex); GlassDialog.Error(this, ex.Message); }
    }

    void Shot_Click(object sender, RoutedEventArgs e) => TakeShot();

    void Freeze_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _layer.Surface.CommitText();
            if (_frozen == null)
            {
                _frozen = CaptureClean();
                _layer.SetBackground(_frozen);
                FreezeText.Text = "Descongelar";
                ModeLive.IsEnabled = false;
                ModeDraw.IsChecked = true;
            }
            else
            {
                _frozen.Dispose();
                _frozen = null;
                _layer.SetBackground(null);
                FreezeText.Text = "Congelar tela";
                ModeLive.IsEnabled = true;
            }
            _layer.SetDrawing(Drawing || _frozen != null);
            ShowHint();
        }
        catch (Exception ex) { Log.Error("Congelar", ex); GlassDialog.Error(this, ex.Message); }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _hintTimer.Stop();
        _frozen?.Dispose();
        _frozen = null;
        _layer.Close();
    }
}

/// <summary>Camada transparente do tamanho do monitor onde ficam os desenhos. No modo "mexer" ela deixa os cliques passarem.</summary>
public sealed class LiveLayer : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;

    public readonly AnnotationSurface Surface = new();
    readonly Border _frame = new() { BorderThickness = new Thickness(3), IsHitTestVisible = false };
    readonly SD.Rectangle _monitor;
    bool _drawing = true;

    public bool Excluded { get; private set; }

    public LiveLayer(SD.Rectangle monitor)
    {
        _monitor = monitor;
        Title = AppInfo.Name;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        var root = new Grid();
        root.Children.Add(Surface);
        root.Children.Add(_frame);
        Content = root;
        SetBackground(null);
        SourceInitialized += (_, _) =>
        {
            Glass.PlacePhysical(this, monitor, topmost: true);
            Excluded = Glass.ExcludeFromCapture(this);
            var h = Glass.Handle(this);
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            SetDrawing(_drawing);
        };
        // Ao desenhar, a camada recebe o teclado (texto, Ctrl+Z).
        PreviewMouseLeftButtonDown += (_, _) => { if (!IsActive) Activate(); };
        Closed += (_, _) => Surface.Release();
        // Garante o handle já aqui, para a paleta saber se a camada fica fora dos prints.
        Glass.Handle(this);
    }

    /// <summary>Fundo da camada: null = transparente (tela viva); um bitmap = tela congelada.</summary>
    public void SetBackground(SD.Bitmap? frozen)
    {
        var marks = Surface.Marks.ToList();
        Surface.Release();
        if (frozen != null) { Surface.Load(frozen, marks); return; }
        using var clear = new SD.Bitmap(_monitor.Width, _monitor.Height, SDI.PixelFormat.Format32bppPArgb);
        Surface.Load(clear, marks);
    }

    /// <summary>Desenhar = a camada pega o mouse. Senão os cliques atravessam para o programa de baixo.</summary>
    public void SetDrawing(bool on)
    {
        _drawing = on;
        // Fundo quase invisível: no Windows, pixel 100% transparente deixa o clique passar.
        Background = on ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent;
        _frame.BorderBrush = on ? new SolidColorBrush(Color.FromArgb(200, 0xFF, 0x3B, 0x4E)) : new SolidColorBrush(Color.FromArgb(150, 0xB4, 0xBB, 0xC4));
        Surface.IsHitTestVisible = on;
        try
        {
            var h = Glass.Handle(this);
            var ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_LAYERED;
            SetWindowLong(h, GWL_EXSTYLE, on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT);
        }
        catch { }
    }
}
