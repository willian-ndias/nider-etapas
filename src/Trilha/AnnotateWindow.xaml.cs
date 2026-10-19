using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using SD = System.Drawing;

namespace Trilha;

/// <summary>
/// Desenho por cima do print. No modo "tela" cobre o monitor com a imagem congelada,
/// como se você desenhasse direto na tela. No modo "editar" abre um passo já gravado.
/// </summary>
public partial class AnnotateWindow : Window
{
    readonly Step _step;
    readonly bool _overlay;
    readonly SD.Bitmap _shot;
    Point _dragStart;
    bool _dragging;

    public AnnotateWindow(Step step, bool overlay, SD.Rectangle monitor, int stepNumber = 0)
    {
        InitializeComponent();
        _step = step;
        _overlay = overlay;
        _shot = step.LoadScreenshot();
        Title = AppInfo.Name;
        Palette.Attach(Surface);
        Surface.Load(_shot, step.Marks);
        PaletteShell.SizeChanged += (_, _) => PaletteShell.Clip = new RectangleGeometry(new Rect(PaletteShell.RenderSize), 22, 22);

        if (overlay)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            OverlayComment.Text = step.Comment;
            SourceInitialized += (_, _) => Glass.PlacePhysical(this, monitor, topmost: true);
            ContentRendered += (_, _) => { UpdateFrost(); Surface.Focus(); };
            // arrasta a paleta pela borda dela
            PaletteShell.MouseLeftButtonDown += PaletteDown;
            PaletteShell.MouseMove += PaletteMove;
            PaletteShell.MouseLeftButtonUp += PaletteUp;
        }
        else
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 48, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
            Background = (Brush)FindResource("WindowBackdrop");
            TitleBar.Visibility = Visibility.Visible;
            Fields.Visibility = Visibility.Visible;
            CommentRow.Visibility = Visibility.Collapsed;
            FrostImage.Visibility = Visibility.Collapsed;
            Grid.SetRow(Floating, 1);
            Floating.Margin = new Thickness(0, 0, 0, 10);
            WindowTitle.Text = stepNumber > 0 ? $"Passo {stepNumber}" : "Editar passo";
            DescriptionBox.Text = step.Description;
            CommentBox.Text = step.Comment;
            // Ocupa quase toda a área útil da tela, centralizada.
            var wa = SystemParameters.WorkArea;
            Width = Math.Round(wa.Width * 0.94); Height = Math.Round(wa.Height * 0.94);
            Left = wa.Left + (wa.Width - Width) / 2; Top = wa.Top + (wa.Height - Height) / 2;
            MinWidth = Math.Min(960, Width); MinHeight = Math.Min(600, Height);
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => Glass.DarkChrome(this);
            StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        }

        PreviewKeyDown += OnKey;
        Closed += (_, _) => { Surface.Release(); _shot.Dispose(); };
    }

    // ------------------------------------------------------------ vidro da paleta

    /// <summary>Usa o pedaço da imagem congelada que fica atrás da paleta, desfocado.</summary>
    void UpdateFrost()
    {
        if (!_overlay || PaletteShell.ActualWidth < 2) return;
        try
        {
            var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var p = PaletteShell.TranslatePoint(new Point(0, 0), Root);
            var r = new SD.Rectangle((int)(p.X * s), (int)(p.Y * s), (int)(PaletteShell.ActualWidth * s), (int)(PaletteShell.ActualHeight * s));
            FrostImage.Background = new ImageBrush(Glass.Crop(_shot, r, 3)) { Stretch = Stretch.Fill };
        }
        catch { }
    }

    void PaletteDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsControl(d)) return;
        _dragging = true;
        _dragStart = e.GetPosition(Root);
        PaletteShell.CaptureMouse();
        e.Handled = true;
    }

    void PaletteMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Root);
        FloatingMove.X += p.X - _dragStart.X;
        FloatingMove.Y += p.Y - _dragStart.Y;
        _dragStart = p;
        UpdateFrost();
    }

    void PaletteUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PaletteShell.ReleaseMouseCapture();
    }

    static bool IsControl(DependencyObject d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase || d is TextBox) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ------------------------------------------------------------ teclado e botões

    void OnKey(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox || Surface.IsEditingText;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!typing && ctrl && e.Key == Key.Z) { Surface.Undo(); e.Handled = true; }
        else if (!typing && ctrl && e.Key == Key.Y) { Surface.Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Enter) { Accept(); e.Handled = true; }
        else if (e.Key == Key.Escape && !Surface.IsEditingText) { DialogResult = false; e.Handled = true; }
    }

    void Accept_Click(object sender, RoutedEventArgs e) => Accept();

    void Accept()
    {
        Surface.CommitText();
        _step.Marks.Clear();
        _step.Marks.AddRange(Surface.Marks);
        if (_overlay)
        {
            var c = OverlayComment.Text.Trim();
            _step.Description = c.Length > 0 ? c : "Observe os destaques na imagem.";
            _step.Comment = "";
        }
        else
        {
            _step.Description = DescriptionBox.Text.Trim();
            _step.Comment = CommentBox.Text.Trim();
        }
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
