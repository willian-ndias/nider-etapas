using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SD = System.Drawing;

namespace Trilha;

/// <summary>Recorta o print de um passo. A área é escolhida sobre o print inteiro (mesmo se já foi recortado antes).</summary>
public partial class CropWindow : Window
{
    /// <summary>Área escolhida, em pixels do print inteiro.</summary>
    public SD.Rectangle Selection { get; private set; }
    public int BorderSize { get; private set; }
    public SD.Color BorderColor { get; private set; }

    readonly int _imgW, _imgH;
    Rect _sel;                       // em pixels do print
    enum Drag { None, New, Move, L, R, T, B, TL, TR, BL, BR }
    Drag _drag;
    Point _start;                    // em pixels do print
    Rect _startSel;
    readonly List<Rectangle> _handles = new();
    const double MinSize = 24;
    bool _ready;

    void Border_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        foreach (RadioButton c in SizeChips.Children) if (c.IsChecked == true) BorderSize = int.Parse((string)c.Tag);
        foreach (RadioButton c in ColorChips.Children)
            if (c.IsChecked == true && c.Background is SolidColorBrush b) BorderColor = SD.Color.FromArgb(b.Color.R, b.Color.G, b.Color.B);
        Redraw();
    }

    public CropWindow(Step step, int number)
    {
        InitializeComponent();
        using (var bmp = step.RenderFull()) { Picture.Source = Glass.ToBitmapSource(bmp); _imgW = bmp.Width; _imgH = bmp.Height; }
        _sel = step.FullPng == null
            ? new Rect(0, 0, _imgW, _imgH)
            : new Rect(step.CropOrigin.X, step.CropOrigin.Y, StepWidth(step), StepHeight(step));
        if (number > 0) WindowTitle.Text = $"Ajustar a imagem do passo {number}";
        BorderSize = step.BorderSize; BorderColor = step.BorderColor;
        ((RadioButton)SizeChips.Children[Math.Clamp(BorderSize, 0, 3)]).IsChecked = true;
        var picked = false;
        foreach (RadioButton c in ColorChips.Children)
            if (c.Background is SolidColorBrush b && b.Color.R == BorderColor.R && b.Color.G == BorderColor.G && b.Color.B == BorderColor.B) { c.IsChecked = true; picked = true; }
        if (!picked) ((RadioButton)ColorChips.Children[0]).IsChecked = true;
        _ready = true;

        for (int i = 0; i < 8; i++)
        {
            var h = new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, Fill = Brushes.White, IsHitTestVisible = false,
                                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 } };
            _handles.Add(h); Overlay.Children.Add(h);
        }

        var wa = SystemParameters.WorkArea;
        Width = Math.Round(wa.Width * 0.94); Height = Math.Round(wa.Height * 0.94);
        Left = wa.Left + (wa.Width - Width) / 2; Top = wa.Top + (wa.Height - Height) / 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Glass.DarkChrome(this);
        Picture.SizeChanged += (_, _) => { Overlay.Width = Picture.ActualWidth; Overlay.Height = Picture.ActualHeight; Redraw(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
        };
    }

    static int StepWidth(Step s) { using var b = s.LoadScreenshot(); return b.Width; }
    static int StepHeight(Step s) { using var b = s.LoadScreenshot(); return b.Height; }

    bool IsFull => _sel.Width >= _imgW - 0.5 && _sel.Height >= _imgH - 0.5;

    double Scale => Picture.ActualWidth > 0 ? _imgW / Picture.ActualWidth : 1;

    Point ToImage(Point p) => new(Math.Clamp(p.X * Scale, 0, _imgW), Math.Clamp(p.Y * Scale, 0, _imgH));

    void Redraw()
    {
        if (Picture.ActualWidth <= 0) return;
        var k = 1 / Scale;
        var r = new Rect(_sel.X * k, _sel.Y * k, _sel.Width * k, _sel.Height * k);
        Shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Overlay.Width, Overlay.Height)), new RectangleGeometry(r));
        Place(Frame, r);
        var bw = Step.BorderPixels(BorderSize, (int)Math.Round(_sel.Width)) * k;
        if (bw > 0)
        {
            BorderPreview.Visibility = Visibility.Visible;
            BorderPreview.Fill = new SolidColorBrush(Color.FromRgb(BorderColor.R, BorderColor.G, BorderColor.B));
            Place(BorderPreview, new Rect(r.X - bw, r.Y - bw, r.Width + 2 * bw, r.Height + 2 * bw));
            BorderPreview.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, r.Width + 2 * bw, r.Height + 2 * bw)), new RectangleGeometry(new Rect(bw, bw, r.Width, r.Height)));
        }
        else BorderPreview.Visibility = Visibility.Collapsed;
        Place(Grid1, new Rect(r.X + r.Width / 3, r.Y, r.Width / 3, r.Height));
        Grid1.Visibility = _drag != Drag.None ? Visibility.Visible : Visibility.Collapsed;
        var pts = new[] { r.TopLeft, new Point(r.X + r.Width / 2, r.Y), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
                          r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.X, r.Y + r.Height / 2) };
        for (int i = 0; i < 8; i++) { Canvas.SetLeft(_handles[i], pts[i].X - 6); Canvas.SetTop(_handles[i], pts[i].Y - 6); }
        SizeText.Text = IsFull ? $"Imagem inteira · {_imgW} × {_imgH}" : $"{(int)Math.Round(_sel.Width)} × {(int)Math.Round(_sel.Height)} pixels";
    }

    static void Place(FrameworkElement e, Rect r)
    {
        Canvas.SetLeft(e, r.X); Canvas.SetTop(e, r.Y); e.Width = Math.Max(0, r.Width); e.Height = Math.Max(0, r.Height);
    }

    Drag HitTest(Point p)
    {
        var tol = 9 * Scale;
        if (IsFull)
        {
            // Com a imagem inteira selecionada, só as alças mexem nas bordas; o resto desenha uma área nova.
            var hs = new[] { (Drag.TL, new Point(0, 0)), (Drag.T, new Point(_imgW / 2.0, 0)), (Drag.TR, new Point(_imgW, 0)),
                             (Drag.R, new Point(_imgW, _imgH / 2.0)), (Drag.BR, new Point(_imgW, _imgH)), (Drag.B, new Point(_imgW / 2.0, _imgH)),
                             (Drag.BL, new Point(0, _imgH)), (Drag.L, new Point(0, _imgH / 2.0)) };
            foreach (var (d, h) in hs)
                if (Math.Abs(p.X - h.X) < tol && Math.Abs(p.Y - h.Y) < tol) return d;
            return Drag.New;
        }
        bool nl = Math.Abs(p.X - _sel.Left) < tol, nr = Math.Abs(p.X - _sel.Right) < tol;
        bool nt = Math.Abs(p.Y - _sel.Top) < tol, nb = Math.Abs(p.Y - _sel.Bottom) < tol;
        bool inX = p.X > _sel.Left - tol && p.X < _sel.Right + tol, inY = p.Y > _sel.Top - tol && p.Y < _sel.Bottom + tol;
        if (nl && nt) return Drag.TL; if (nr && nt) return Drag.TR;
        if (nl && nb) return Drag.BL; if (nr && nb) return Drag.BR;
        if (nl && inY) return Drag.L; if (nr && inY) return Drag.R;
        if (nt && inX) return Drag.T; if (nb && inX) return Drag.B;
        if (_sel.Contains(p)) return Drag.Move;
        return Drag.New;
    }

    static Cursor CursorFor(Drag d) => d switch
    {
        Drag.L or Drag.R => Cursors.SizeWE,
        Drag.T or Drag.B => Cursors.SizeNS,
        Drag.TL or Drag.BR => Cursors.SizeNWSE,
        Drag.TR or Drag.BL => Cursors.SizeNESW,
        Drag.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        _start = ToImage(e.GetPosition(Overlay));
        _drag = HitTest(_start);
        _startSel = _sel;
        Overlay.CaptureMouse();
        Redraw();
    }

    void Overlay_Move(object sender, MouseEventArgs e)
    {
        var p = ToImage(e.GetPosition(Overlay));
        if (_drag == Drag.None) { Overlay.Cursor = CursorFor(HitTest(p)); return; }
        double dx = p.X - _start.X, dy = p.Y - _start.Y;
        double l = _startSel.Left, t = _startSel.Top, r = _startSel.Right, b = _startSel.Bottom;
        switch (_drag)
        {
            case Drag.New:
                l = Math.Min(_start.X, p.X); r = Math.Max(_start.X, p.X);
                t = Math.Min(_start.Y, p.Y); b = Math.Max(_start.Y, p.Y);
                break;
            case Drag.Move:
                dx = Math.Clamp(dx, -l, _imgW - r); dy = Math.Clamp(dy, -t, _imgH - b);
                l += dx; r += dx; t += dy; b += dy;
                break;
            default:
                if (_drag is Drag.L or Drag.TL or Drag.BL) l = Math.Min(p.X, r - MinSize);
                if (_drag is Drag.R or Drag.TR or Drag.BR) r = Math.Max(p.X, l + MinSize);
                if (_drag is Drag.T or Drag.TL or Drag.TR) t = Math.Min(p.Y, b - MinSize);
                if (_drag is Drag.B or Drag.BL or Drag.BR) b = Math.Max(p.Y, t + MinSize);
                break;
        }
        l = Math.Max(0, l); t = Math.Max(0, t); r = Math.Min(_imgW, r); b = Math.Min(_imgH, b);
        if (r - l >= 1 && b - t >= 1) _sel = new Rect(l, t, r - l, b - t);
        Redraw();
    }

    void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        Overlay.ReleaseMouseCapture();
        // Um clique solto (sem arrastar) não vira um recorte minúsculo.
        if (_drag == Drag.New && (_sel.Width < MinSize || _sel.Height < MinSize)) _sel = _startSel;
        _drag = Drag.None;
        Redraw();
    }

    void Full_Click(object sender, RoutedEventArgs e) { _sel = new Rect(0, 0, _imgW, _imgH); Redraw(); }

    void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    void Apply()
    {
        int x = (int)Math.Round(_sel.X), y = (int)Math.Round(_sel.Y);
        int r = (int)Math.Round(_sel.Right), b = (int)Math.Round(_sel.Bottom);
        Selection = SD.Rectangle.FromLTRB(x, y, Math.Min(_imgW, r), Math.Min(_imgH, b));
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
