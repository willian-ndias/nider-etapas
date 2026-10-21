using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

namespace Trilha;

public enum ClickShape { Dot, Circle, BigCircle, Square, Arrow, Spotlight, Blur }

/// <summary>Como o clique aparece no print: formato, cor e se contorna o botão clicado.</summary>
public sealed class ClickStyle
{
    public ClickShape Shape = ClickShape.Circle;
    public Color Color = Color.FromArgb(255, 77, 106);
    /// <summary>Contorno azul no botão/campo que foi clicado (quando o Windows informa onde ele está).</summary>
    public bool Outline = true;

    public ClickStyle Copy() => (ClickStyle)MemberwiseClone();

    public bool SameAs(ClickStyle o) => Shape == o.Shape && Color.ToArgb() == o.Color.ToArgb() && Outline == o.Outline;

    public static readonly (ClickShape Shape, string Name)[] Shapes =
    {
        (ClickShape.Dot, "Ponto"), (ClickShape.Circle, "Círculo"), (ClickShape.BigCircle, "Círculo grande"),
        (ClickShape.Square, "Quadrado"), (ClickShape.Arrow, "Seta"), (ClickShape.Spotlight, "Holofote"), (ClickShape.Blur, "Desfoque"),
    };

    public static readonly (string Name, Color Color)[] Colors =
    {
        ("Vermelho", Color.FromArgb(255, 77, 106)), ("Laranja", Color.FromArgb(255, 159, 67)), ("Amarelo", Color.FromArgb(255, 212, 59)),
        ("Verde", Color.FromArgb(46, 212, 122)), ("Azul", Color.FromArgb(46, 197, 255)), ("Roxo", Color.FromArgb(139, 123, 255)),
        ("Branco", Color.FromArgb(255, 255, 255)), ("Preto", Color.FromArgb(28, 31, 42)),
    };

    // ------------------------------------------------------------ o marcador escolhido (vale para as próximas gravações)

    static ClickStyle? _current;
    static string PathFile => System.IO.Path.Combine(AppInfo.DataDir, "marcador.txt");

    public static ClickStyle Current
    {
        get => (_current ??= Load()).Copy();
        set { _current = value.Copy(); Save(_current); }
    }

    static ClickStyle Load()
    {
        var s = new ClickStyle();
        try
        {
            if (!File.Exists(PathFile)) return s;
            var p = File.ReadAllText(PathFile).Trim().Split(';');
            if (p.Length > 0 && Enum.TryParse<ClickShape>(p[0], out var shape)) s.Shape = shape;
            if (p.Length > 1 && p[1].Length == 6) s.Color = Color.FromArgb(Convert.ToInt32(p[1], 16) | unchecked((int)0xFF000000));
            if (p.Length > 2) s.Outline = p[2] != "0";
        }
        catch { }
        return s;
    }

    static void Save(ClickStyle s)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(PathFile, $"{s.Shape};{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2};{(s.Outline ? 1 : 0)}");
        }
        catch (Exception ex) { Log.Error("Marcador", ex); }
    }
}

/// <summary>Destaque automático do clique, no formato e cor escolhidos. Fica separado do print: dá para trocar depois.</summary>
public sealed class ClickMark : Mark
{
    public Point Center;
    public Rectangle? Element;
    public ClickStyle Style = ClickStyle.Current;

    public override Mark Clone() { var c = (ClickMark)base.Clone(); c.Style = Style.Copy(); return c; }

    public override void Move(int dx, int dy)
    {
        Center.Offset(dx, dy);
        if (Element is Rectangle e) { e.Offset(dx, dy); Element = e; }
    }

    /// <summary>Área para selecionar e arrastar o marcador: o botão clicado ou um círculo em volta do clique.</summary>
    public override RectangleF Area() =>
        Element is Rectangle e && e.Width > 4 && e.Height > 4
            ? RectangleF.Inflate(e, 8, 8)
            : new RectangleF(Center.X - 40, Center.Y - 40, 80, 80);

    bool HasElement(RectangleF bounds) =>
        Element is Rectangle e && e.Width > 4 && e.Height > 4 && e.Width * (float)e.Height < bounds.Width * bounds.Height * 0.5f;

    /// <summary>Área que fica em destaque no holofote/desfoque: o botão clicado ou um círculo em volta do clique.</summary>
    GraphicsPath FocusPath(RectangleF bounds)
    {
        var p = new GraphicsPath();
        if (HasElement(bounds)) BoxMark.AddRounded(p, RectangleF.Inflate(Element!.Value, 8, 8), 10);
        else p.AddEllipse(Center.X - 64, Center.Y - 64, 128, 128);
        return p;
    }

    bool UsesBackdrop => Style.Shape is ClickShape.Spotlight or ClickShape.Blur;

    /// <summary>Escurece ou desfoca tudo fora do destaque. Roda antes de todos os outros desenhos.</summary>
    internal void DrawBackdrop(Graphics g, Bitmap target)
    {
        if (!UsesBackdrop) return;
        var state = g.Save();
        try
        {
            var bounds = g.VisibleClipBounds;
            using var focus = FocusPath(bounds);
            using var outside = new Region(bounds);
            outside.Exclude(focus);
            if (Style.Shape == ClickShape.Blur)
            {
                // Desfoque barato: reduz bastante e amplia de volta.
                var m = g.Transform;
                float ox = m.OffsetX, oy = m.OffsetY;
                int sw = Math.Max(1, target.Width / 14), sh = Math.Max(1, target.Height / 14);
                using var small = new Bitmap(sw, sh);
                using (var sg = Graphics.FromImage(small))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    sg.DrawImage(target, 0, 0, sw, sh);
                }
                g.SetClip(outside, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, new RectangleF(-ox, -oy, target.Width, target.Height));
                using var dim = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
                g.FillRegion(dim, outside);
            }
            else
            {
                using var dim = new SolidBrush(Color.FromArgb(170, 8, 10, 13));
                g.FillRegion(dim, outside);
            }
        }
        finally { g.Restore(state); }
    }

    public override void Draw(Graphics g)
    {
        var bounds = g.VisibleClipBounds;
        var c = Style.Color;
        if (Style.Outline && HasElement(bounds) && !UsesBackdrop)
        {
            using var path = new GraphicsPath();
            BoxMark.AddRounded(path, RectangleF.Inflate(Element!.Value, 3, 3), 6);
            using var glow = new Pen(Color.FromArgb(70, 46, 197, 255), 7);
            g.DrawPath(glow, path);
            using var line = new Pen(Color.FromArgb(240, 46, 160, 255), 2.5f);
            g.DrawPath(line, path);
        }
        switch (Style.Shape)
        {
            case ClickShape.Dot:
            {
                const int r = 8;
                using (var sh = new SolidBrush(Shadow)) g.FillEllipse(sh, Center.X - r - 1, Center.Y - r + 1, r * 2 + 2, r * 2 + 2);
                using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, Center.X - r - 2.5f, Center.Y - r - 2.5f, r * 2 + 5, r * 2 + 5);
                using var dot = new SolidBrush(c);
                g.FillEllipse(dot, Center.X - r, Center.Y - r, r * 2, r * 2);
                break;
            }
            case ClickShape.Circle:
            case ClickShape.BigCircle:
            {
                var big = Style.Shape == ClickShape.BigCircle;
                float r = big ? 36 : 17;
                using (var fill = new SolidBrush(Color.FromArgb(big ? 60 : 80, c)))
                    g.FillEllipse(fill, Center.X - r, Center.Y - r, r * 2, r * 2);
                using (var ring = new Pen(Color.FromArgb(245, c), big ? 4 : 3))
                    g.DrawEllipse(ring, Center.X - r, Center.Y - r, r * 2, r * 2);
                using var dot = new SolidBrush(c);
                float d = big ? 5 : 4;
                g.FillEllipse(dot, Center.X - d, Center.Y - d, d * 2, d * 2);
                break;
            }
            case ClickShape.Square:
            {
                var r = new RectangleF(Center.X - 22, Center.Y - 22, 44, 44);
                using var path = new GraphicsPath();
                BoxMark.AddRounded(path, r, 8);
                using (var fill = new SolidBrush(Color.FromArgb(60, c))) g.FillPath(fill, path);
                using (var sh = new Pen(Shadow, 5.5f)) { g.TranslateTransform(1.5f, 2f); g.DrawPath(sh, path); g.TranslateTransform(-1.5f, -2f); }
                using var pen = new Pen(c, 3.5f) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                break;
            }
            case ClickShape.Arrow:
            {
                // A seta vem de baixo à direita; perto da borda, ela vira para caber na imagem.
                int sx = Center.X + 80 > bounds.Right ? -1 : 1;
                int sy = Center.Y + 80 > bounds.Bottom ? -1 : 1;
                new ArrowMark
                {
                    Color = c, Width = 6,
                    From = new Point(Center.X + sx * 78, Center.Y + sy * 70),
                    To = new Point(Center.X + sx * 6, Center.Y + sy * 6),
                }.Draw(g);
                break;
            }
            case ClickShape.Spotlight:
            case ClickShape.Blur:
            {
                using var focus = FocusPath(bounds);
                using (var sh = new Pen(Shadow, 5)) { g.TranslateTransform(1f, 1.5f); g.DrawPath(sh, focus); g.TranslateTransform(-1f, -1.5f); }
                using var pen = new Pen(c, 3) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, focus);
                break;
            }
        }
    }
}

public static class MarkRender
{
    /// <summary>Desenha tudo em cima do print: primeiro holofote/desfoque dos cliques, depois o resto.</summary>
    public static void DrawAll(Graphics g, Bitmap target, IEnumerable<Mark> marks)
    {
        var list = marks as IList<Mark> ?? marks.ToList();
        foreach (var c in list.OfType<ClickMark>()) c.DrawBackdrop(g, target);
        foreach (var m in list) m.Draw(g);
    }
}
