using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace Trilha;

/// <summary>
/// Área de desenho em WPF. O desenho é feito com o mesmo código que gera o PDF,
/// então o que aparece na tela é exatamente o que vai para o manual.
/// </summary>
public sealed class AnnotationSurface : Grid
{
    readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    readonly Grid _inner = new();
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Canvas _overlay = new();

    SD.Bitmap? _base, _committed, _frame;
    WriteableBitmap? _wb;
    bool _dirty;

    public List<Mark> Marks { get; private set; } = new();
    readonly Stack<Mark> _redo = new();
    Mark? _current;

    TextBox? _editor;
    SD.Point _editorAt;

    public Tool Tool { get; set; } = Tool.Arrow;
    public SD.Color DrawColor { get; set; } = SD.Color.FromArgb(255, 77, 106);
    public float DrawWidth { get; set; } = 5;

    public event Action? Changed;

    public AnnotationSurface()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _inner.Children.Add(_image);
        _inner.Children.Add(_overlay);
        _viewbox.Child = _inner;
        Children.Add(_viewbox);
        _inner.Cursor = Cursors.Cross;

        _inner.MouseLeftButtonDown += OnDown;
        _inner.MouseMove += OnMove;
        _inner.MouseLeftButtonUp += OnUp;
        CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public void Load(SD.Bitmap screenshot, IEnumerable<Mark> marks)
    {
        _base = new SD.Bitmap(screenshot.Width, screenshot.Height, SDI.PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(_base)) g.DrawImage(screenshot, 0, 0, screenshot.Width, screenshot.Height);
        _committed = new SD.Bitmap(_base.Width, _base.Height, SDI.PixelFormat.Format32bppPArgb);
        _frame = new SD.Bitmap(_base.Width, _base.Height, SDI.PixelFormat.Format32bppPArgb);
        _wb = new WriteableBitmap(_base.Width, _base.Height, 96, 96, PixelFormats.Pbgra32, null);
        _image.Source = _wb;
        Marks = marks.ToList();
        _redo.Clear();
        Loaded += (_, _) => SizeInner();
        SizeInner();
        Rebuild();
    }

    void SizeInner()
    {
        if (_base == null) return;
        var s = DpiScale;
        _inner.Width = _base.Width / s;
        _inner.Height = _base.Height / s;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SizeInner();
    }

    // ------------------------------------------------------------ desenho

    static void Smooth(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
        g.CompositingQuality = SD.Drawing2D.CompositingQuality.HighSpeed;
    }

    void Rebuild()
    {
        if (_base == null || _committed == null) return;
        using (var g = SD.Graphics.FromImage(_committed))
        {
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(_base, 0, 0);
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceOver;
            Smooth(g);
            MarkRender.DrawAll(g, _committed, Marks);
        }
        _dirty = true;
        Present();
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (_dirty) Present();
    }

    void Present()
    {
        if (_wb == null || _committed == null || _frame == null) return;
        _dirty = false;
        var src = _committed;
        if (_current != null)
        {
            using var g = SD.Graphics.FromImage(_frame);
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(_committed, 0, 0);
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceOver;
            Smooth(g);
            _current.Draw(g);
            src = _frame;
        }
        var rect = new SD.Rectangle(0, 0, src.Width, src.Height);
        var data = src.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppPArgb);
        try
        {
            _wb.WritePixels(new Int32Rect(0, 0, src.Width, src.Height), data.Scan0, data.Stride * src.Height, data.Stride);
        }
        finally { src.UnlockBits(data); }
    }

    SD.Point ToImage(MouseEventArgs e)
    {
        var p = e.GetPosition(_inner);
        var s = DpiScale;
        return new SD.Point((int)Math.Round(p.X * s), (int)Math.Round(p.Y * s));
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_base == null) return;
        if (_editor != null) { CommitText(); e.Handled = true; return; }
        var p = ToImage(e);
        switch (Tool)
        {
            case Tool.Text:
                StartText(p);
                e.Handled = true;
                return;
            case Tool.Number:
                Add(new NumberMark { Center = p, Number = MarkFactory.NextNumber(Marks), Color = DrawColor, Width = DrawWidth });
                return;
        }
        _current = MarkFactory.Begin(Tool, p, DrawColor, DrawWidth);
        _inner.CaptureMouse();
        _dirty = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_current == null) return;
        var p = ToImage(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) p = MarkFactory.Constrain(_current, p);
        MarkFactory.Update(_current, p);
        _dirty = true;
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_current == null) return;
        _inner.ReleaseMouseCapture();
        var m = _current;
        _current = null;
        if (MarkFactory.IsMeaningful(m)) Add(m);
        else _dirty = true;
    }

    void Add(Mark m)
    {
        Marks.Add(m);
        _redo.Clear();
        Rebuild();
        Changed?.Invoke();
    }

    public bool CanUndo => Marks.Count > 0;

    public void Undo()
    {
        CommitText();
        if (Marks.Count == 0) return;
        _redo.Push(Marks[^1]);
        Marks.RemoveAt(Marks.Count - 1);
        Rebuild();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        Marks.Add(_redo.Pop());
        Rebuild();
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        CommitText();
        for (int i = Marks.Count - 1; i >= 0; i--) _redo.Push(Marks[i]);
        Marks.Clear();
        Rebuild();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ texto

    public bool IsEditingText => _editor != null;

    void StartText(SD.Point at)
    {
        _editorAt = at;
        var s = DpiScale;
        var px = TextMark.FontPxFor(DrawWidth) / s;
        var c = DrawColor;
        var dark = c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 170;
        _editor = new TextBox
        {
            AcceptsReturn = true,
            MinWidth = px * 6,
            FontSize = px,
            FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI, Global User Interface"),
            Foreground = dark ? new SolidColorBrush(Color.FromRgb(25, 25, 30)) : Brushes.White,
            CaretBrush = dark ? Brushes.Black : Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(px * 0.35, px * 0.15, px * 0.35, px * 0.15),
        };
        _editor.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { ev.Handled = true; CommitText(); }
            else if (ev.Key == Key.Escape) { ev.Handled = true; CancelText(); }
        };
        _editor.LostKeyboardFocus += (_, _) => CommitText();
        Canvas.SetLeft(_editor, at.X / s);
        Canvas.SetTop(_editor, at.Y / s);
        _overlay.Children.Add(_editor);
        Dispatcher.BeginInvoke(() => { _editor?.Focus(); Keyboard.Focus(_editor); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void CommitText()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        _overlay.Children.Remove(ed);
        var text = ed.Text.TrimEnd();
        if (text.Length > 0) Add(new TextMark { At = _editorAt, Text = text, Color = DrawColor, Width = DrawWidth });
    }

    void CancelText()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        _overlay.Children.Remove(ed);
    }

    public void Release()
    {
        _base?.Dispose(); _committed?.Dispose(); _frame?.Dispose();
        _base = _committed = _frame = null;
    }
}
