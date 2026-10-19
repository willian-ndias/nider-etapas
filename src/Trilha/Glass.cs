using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace Trilha;

/// <summary>Ajudas para as janelas de vidro: fundo desfocado de verdade, fora dos prints, etc.</summary>
public static class Glass
{
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out Native.RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>A janela não aparece em prints (Windows 10 2004 ou mais novo). Retorna se funcionou.</summary>
    public static bool ExcludeFromCapture(Window w)
    {
        try { return SetWindowDisplayAffinity(Handle(w), 0x11); } catch { return false; }
    }

    /// <summary>Tema escuro nas bordas do Windows 11 (barra de título, cantos).</summary>
    public static void DarkChrome(Window w)
    {
        try
        {
            var h = Handle(w);
            int on = 1;
            DwmSetWindowAttribute(h, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(h, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
        }
        catch { }
    }

    /// <summary>Posiciona a janela exatamente sobre uma área em pixels físicos (ex.: um monitor inteiro).</summary>
    public static void PlacePhysical(Window w, SD.Rectangle r, bool topmost)
    {
        var h = Handle(w);
        SetWindowPos(h, topmost ? new IntPtr(-1) : IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0040 /*SHOWWINDOW*/);
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>Esconde/mostra a janela sem mexer no foco (um menu aberto em outro programa continua aberto).</summary>
    public static void ShowNoActivate(Window w, bool show) => ShowWindow(Handle(w), show ? 8 /*SW_SHOWNA*/ : 0 /*SW_HIDE*/);

    public static SD.Rectangle PhysicalBounds(Window w)
    {
        GetWindowRect(Handle(w), out var r);
        return SD.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Permite arrastar a janela segurando o botão esquerdo em qualquer área livre.</summary>
    public static void MakeDraggable(Window w, UIElement handle)
    {
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            try { w.DragMove(); } catch { }
        };
    }

    static bool IsInsideButton(DependencyObject d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase || d is TextBox) return true;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ------------------------------------------------------------ conversão de imagens

    public static BitmapSource ToBitmapSource(SD.Bitmap bmp)
    {
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppPArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }

    public static BitmapSource Crop(SD.Bitmap source, SD.Rectangle area, int downscale = 3)
    {
        area.Intersect(new SD.Rectangle(0, 0, source.Width, source.Height));
        if (area.Width < 2 || area.Height < 2) area = new SD.Rectangle(0, 0, 2, 2);
        var w = Math.Max(1, area.Width / downscale);
        var h = Math.Max(1, area.Height / downscale);
        using var small = new SD.Bitmap(w, h, SDI.PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(small))
        {
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, new SD.Rectangle(0, 0, w, h), area, SD.GraphicsUnit.Pixel);
        }
        return ToBitmapSource(small);
    }
}

/// <summary>
/// "Vidro líquido": tira um print do que está atrás da janela, desfoca e usa como fundo.
/// Só funciona se a janela estiver fora dos prints; senão o vidro fica só translúcido.
/// </summary>
public sealed class Frost
{
    readonly Window _window;
    readonly FrameworkElement _area;
    readonly Border _target;
    readonly DispatcherTimer _timer;
    readonly bool _enabled;
    DateTime _lastMove;

    public Frost(Window window, FrameworkElement area, Border target, bool excludedFromCapture)
    {
        _window = window;
        _area = area;
        _target = target;
        // TRILHA_FROST=1 força o vidro ligado (só para testes).
        _enabled = excludedFromCapture || Environment.GetEnvironmentVariable("TRILHA_FROST") == "1";
        _target.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 9, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        _timer.Tick += (_, _) => Update();
        if (!_enabled) { _target.Visibility = Visibility.Collapsed; return; }
        window.LocationChanged += (_, _) =>
        {
            if ((DateTime.Now - _lastMove).TotalMilliseconds > 60) { _lastMove = DateTime.Now; Update(); }
        };
        window.SizeChanged += (_, _) => Update();
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) _timer.Start(); else _timer.Stop(); };
        window.ContentRendered += (_, _) => { Update(); _timer.Start(); };
    }

    public void Update()
    {
        if (!_enabled || !_window.IsVisible) return;
        try
        {
            // PointToScreen já devolve pixels físicos.
            var a = _area.PointToScreen(new Point(0, 0));
            var b = _area.PointToScreen(new Point(_area.ActualWidth, _area.ActualHeight));
            var r = SD.Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);
            if (r.Width < 4 || r.Height < 4) return;
            using var shot = Imaging.Capture(r);
            _target.Background = new ImageBrush(Glass.Crop(shot, new SD.Rectangle(0, 0, r.Width, r.Height), 3)) { Stretch = Stretch.Fill };
        }
        catch { }
    }
}
