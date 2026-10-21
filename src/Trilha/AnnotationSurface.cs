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
    /// <summary>O texto já existente que está sendo editado (clique duplo), e o que ele dizia antes.</summary>
    TextMark? _editing;
    string _editingWas = "";

    /// <summary>Texto selecionado com um clique: dá para arrastar, editar (clique duplo) ou apagar (Delete).</summary>
    TextMark? _selected;
    TextMark? _dragging;
    SD.Point _dragFrom, _dragAt;
    bool _dragMoved;

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

        Focusable = true;
        KeyDown += OnKey;
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
        // Cópias: mover ou editar um texto aqui não muda o passo até a janela salvar (Cancelar desfaz tudo).
        Marks = marks.Select(m => m.Clone()).ToList();
        _selected = _dragging = _editing = null;
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
        if (_selected != null && Marks.Contains(_selected))
        {
            if (src != _frame)
            {
                using var g0 = SD.Graphics.FromImage(_frame);
                g0.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
                g0.DrawImageUnscaled(_committed, 0, 0);
                src = _frame;
            }
            using var g = SD.Graphics.FromImage(_frame);
            Smooth(g);
            var b = _selected.Bounds();
            b.Inflate(5, 5);
            var w = Math.Max(2f, (float)DpiScale * 1.5f);
            using var halo = new SD.Pen(SD.Color.FromArgb(150, 0, 0, 0), w + 2);
            using var dash = new SD.Pen(SD.Color.White, w) { DashStyle = SD.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(halo, b.X, b.Y, b.Width, b.Height);
            g.DrawRectangle(dash, b.X, b.Y, b.Width, b.Height);
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

    /// <summary>O texto mais de cima sob o ponto, ou nenhum.</summary>
    TextMark? TextAt(SD.Point p)
    {
        for (int i = Marks.Count - 1; i >= 0; i--)
        {
            if (Marks[i] is not TextMark t || string.IsNullOrWhiteSpace(t.Text)) continue;
            var b = t.Bounds();
            b.Inflate(4, 4);
            if (b.Contains(p)) return t;
        }
        return null;
    }

    void Select(TextMark? t)
    {
        if (_selected == t) return;
        _selected = t;
        _dirty = true;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor != null || _selected == null) return;
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            Marks.Remove(_selected);
            _selected = null;
            Rebuild();
            Changed?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || e.Key == Key.F2) { EditExisting(_selected); e.Handled = true; }
        else if (e.Key == Key.Escape) { Select(null); e.Handled = true; }
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_base == null) return;
        if (_editor != null) { CommitText(); e.Handled = true; return; }
        var p = ToImage(e);
        // Um texto já colocado: um clique seleciona e arrasta; clique duplo edita.
        if (TextAt(p) is TextMark hit)
        {
            Focus();
            Select(hit);
            if (e.ClickCount >= 2) { _dragging = null; EditExisting(hit); }
            else
            {
                _dragging = hit;
                _dragFrom = p;
                _dragAt = hit.At;
                _dragMoved = false;
                _inner.CaptureMouse();
            }
            e.Handled = true;
            return;
        }
        Select(null);
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
        if (_dragging != null)
        {
            var q = ToImage(e);
            var at = new SD.Point(_dragAt.X + q.X - _dragFrom.X, _dragAt.Y + q.Y - _dragFrom.Y);
            if (at != _dragging.At)
            {
                _dragging.At = at;
                _dragMoved = true;
                Rebuild();
            }
            return;
        }
        if (_current == null)
        {
            // Sobre um texto: a mãozinha de mover avisa que dá para arrastar.
            _inner.Cursor = _base != null && TextAt(ToImage(e)) != null ? Cursors.SizeAll : (Tool == Tool.Text ? Cursors.IBeam : Cursors.Cross);
            return;
        }
        var p = ToImage(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) p = MarkFactory.Constrain(_current, p);
        MarkFactory.Update(_current, p);
        _dirty = true;
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging != null)
        {
            _inner.ReleaseMouseCapture();
            _dragging = null;
            if (_dragMoved) Changed?.Invoke();
            return;
        }
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

    /// <summary>Clique duplo num texto: abre o mesmo editor no lugar dele, com o texto e a cor dele.</summary>
    void EditExisting(TextMark t)
    {
        _editing = t;
        _editingWas = t.Text;
        t.Text = "";            // some da imagem enquanto o editor está aberto
        Rebuild();
        StartText(t.At, t.Color, t.Width, _editingWas);
    }

    void StartText(SD.Point at) => StartText(at, DrawColor, DrawWidth, "");

    void StartText(SD.Point at, SD.Color c, float width, string initial)
    {
        _editorAt = at;
        var s = DpiScale;
        var px = TextMark.FontPxFor(width) / s;
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
            Text = initial,
        };
        _editor.CaretIndex = initial.Length;
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
        if (_editing is TextMark t)
        {
            // Texto já existente: muda no lugar (apagar tudo remove o texto).
            _editing = null;
            if (text.Length > 0) t.Text = text;
            else { Marks.Remove(t); if (_selected == t) _selected = null; }
            Rebuild();
            Changed?.Invoke();
            return;
        }
        if (text.Length > 0) Add(new TextMark { At = _editorAt, Text = text, Color = DrawColor, Width = DrawWidth });
    }

    void CancelText()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        _overlay.Children.Remove(ed);
        if (_editing is TextMark t)
        {
            t.Text = _editingWas;
            _editing = null;
            Rebuild();
        }
    }

    public void Release()
    {
        _base?.Dispose(); _committed?.Dispose(); _frame?.Dispose();
        _base = _committed = _frame = null;
    }
}
