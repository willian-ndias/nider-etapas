using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace Trilha;

public enum Tool { Pen, Highlighter, Arrow, Rectangle, Ellipse, Text, Number }

/// <summary>Um desenho por cima do print (coordenadas em pixels da imagem).</summary>
public abstract class Mark
{
    public Color Color = Color.FromArgb(255, 77, 106);
    public float Width = 5;
    public abstract void Draw(Graphics g);
    /// <summary>Desloca o desenho (usado quando o print é recortado).</summary>
    public abstract void Move(int dx, int dy);
    /// <summary>Cópia independente (a paleta ao vivo usa os mesmos desenhos em vários prints).</summary>
    public virtual Mark Clone() => (Mark)MemberwiseClone();

    protected static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Sombra suave para o desenho se destacar em qualquer fundo.</summary>
    protected static Color Shadow => Color.FromArgb(70, 0, 0, 0);
}

public sealed class PenMark : Mark
{
    public List<Point> Points = new();
    public bool Highlighter;

    public override Mark Clone() { var c = (PenMark)base.Clone(); c.Points = new List<Point>(Points); return c; }

    public override void Move(int dx, int dy) { for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy); }

    public override void Draw(Graphics g)
    {
        if (Points.Count == 0) return;
        var color = Highlighter ? Color.FromArgb(105, Color) : Color;
        var width = Highlighter ? Width * 4 : Width;
        if (Points.Count == 1)
        {
            using var b = new SolidBrush(color);
            g.FillEllipse(b, Points[0].X - width / 2, Points[0].Y - width / 2, width, width);
            return;
        }
        var pts = Points.ToArray();
        if (!Highlighter)
        {
            using var sh = new Pen(Shadow, width + 2) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.TranslateTransform(1.5f, 2f);
            g.DrawLines(sh, pts);
            g.TranslateTransform(-1.5f, -2f);
        }
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }
}

public sealed class ArrowMark : Mark
{
    public Point From, To;

    public override void Move(int dx, int dy) { From.Offset(dx, dy); To.Offset(dx, dy); }

    public override void Draw(Graphics g)
    {
        float dx = To.X - From.X, dy = To.Y - From.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;
        g.TranslateTransform(1.5f, 2f);
        Paint(g, Shadow, 2);
        g.TranslateTransform(-1.5f, -2f);
        Paint(g, Color, 0);
    }

    void Paint(Graphics g, Color c, float extra)
    {
        float dx = To.X - From.X, dy = To.Y - From.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        float ux = dx / len, uy = dy / len;
        var head = Math.Min(Math.Max(18f, Width * 4.5f), len * 0.8f);
        var baseX = To.X - ux * head * 0.85f;
        var baseY = To.Y - uy * head * 0.85f;
        var half = head * 0.55f;
        using (var pen = new Pen(c, Width + extra) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
            g.DrawLine(pen, From.X, From.Y, baseX, baseY);
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(To.X + ux * extra, To.Y + uy * extra),
            new PointF(To.X - ux * head - uy * half, To.Y - uy * head + ux * half),
            new PointF(baseX, baseY),
            new PointF(To.X - ux * head + uy * half, To.Y - uy * head - ux * half),
        });
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }
}

public sealed class BoxMark : Mark
{
    public Point A, B;
    public bool Ellipse;

    public override void Move(int dx, int dy) { A.Offset(dx, dy); B.Offset(dx, dy); }

    public override void Draw(Graphics g)
    {
        var r = Normalize(A, B);
        if (r.Width < 1 && r.Height < 1) return;
        using var path = new GraphicsPath();
        if (Ellipse) path.AddEllipse(r);
        else AddRounded(path, r, Math.Min(10, Math.Min(r.Width, r.Height) / 4));
        using (var sh = new Pen(Shadow, Width + 2) { LineJoin = LineJoin.Round })
        {
            g.TranslateTransform(1.5f, 2f);
            g.DrawPath(sh, path);
            g.TranslateTransform(-1.5f, -2f);
        }
        using var pen = new Pen(Color, Width) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
    }

    internal static void AddRounded(GraphicsPath p, RectangleF r, float radius)
    {
        if (radius < 1) { p.AddRectangle(r); return; }
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
    }
}

public sealed class TextMark : Mark
{
    public Point At;
    public string Text = "";

    public float FontPx => 14 + Width * 3;
    public static float FontPxFor(float width) => 14 + width * 3;

    public override void Move(int dx, int dy) => At.Offset(dx, dy);

    public override void Draw(Graphics g)
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        using var font = new Font("Segoe UI Semibold", FontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        var size = g.MeasureString(Text, font);
        var padX = FontPx * 0.45f;
        var padY = FontPx * 0.22f;
        var box = new RectangleF(At.X, At.Y, size.Width + padX * 2, size.Height + padY * 2);
        using var path = new GraphicsPath();
        BoxMark.AddRounded(path, box, FontPx * 0.45f);
        using (var shadow = new GraphicsPath())
        {
            BoxMark.AddRounded(shadow, new RectangleF(box.X + 2, box.Y + 3, box.Width, box.Height), FontPx * 0.45f);
            using var sb = new SolidBrush(Shadow);
            g.FillPath(sb, shadow);
        }
        using (var bg = new SolidBrush(Color))
            g.FillPath(bg, path);
        var dark = Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114 > 170;
        using var tb = new SolidBrush(dark ? Color.FromArgb(25, 25, 30) : Color.White);
        g.DrawString(Text, font, tb, At.X + padX, At.Y + padY);
    }
}

/// <summary>Bolinha numerada (1, 2, 3...) para indicar a ordem das coisas na imagem.</summary>
public sealed class NumberMark : Mark
{
    public Point Center;
    public int Number = 1;

    public override void Move(int dx, int dy) => Center.Offset(dx, dy);

    public override void Draw(Graphics g)
    {
        var r = 13 + Width * 1.6f;
        using (var sb = new SolidBrush(Shadow))
            g.FillEllipse(sb, Center.X - r + 1.5f, Center.Y - r + 2.5f, r * 2, r * 2);
        using (var b = new SolidBrush(Color))
            g.FillEllipse(b, Center.X - r, Center.Y - r, r * 2, r * 2);
        using (var ring = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(2, r / 7)))
            g.DrawEllipse(ring, Center.X - r, Center.Y - r, r * 2, r * 2);
        using var font = new Font("Segoe UI", r * 1.05f, FontStyle.Bold, GraphicsUnit.Pixel);
        var text = Number.ToString();
        var size = g.MeasureString(text, font);
        using var tb = new SolidBrush(Color.White);
        g.DrawString(text, font, tb, Center.X - size.Width / 2, Center.Y - size.Height / 2);
    }
}

public static class MarkFactory
{
    public static Mark? Begin(Tool tool, Point p, Color color, float width) => tool switch
    {
        Tool.Pen => new PenMark { Color = color, Width = width, Points = { p } },
        Tool.Highlighter => new PenMark { Color = color, Width = width, Highlighter = true, Points = { p } },
        Tool.Arrow => new ArrowMark { Color = color, Width = width, From = p, To = p },
        Tool.Rectangle => new BoxMark { Color = color, Width = width, A = p, B = p },
        Tool.Ellipse => new BoxMark { Color = color, Width = width, A = p, B = p, Ellipse = true },
        _ => null,
    };

    public static void Update(Mark s, Point p)
    {
        switch (s)
        {
            case PenMark pen:
                if (pen.Points.Count == 0 || pen.Points[^1] != p) pen.Points.Add(p);
                break;
            case ArrowMark a: a.To = p; break;
            case BoxMark b: b.B = p; break;
        }
    }

    /// <summary>Com Shift: setas retas (0/45/90°) e quadrados/círculos perfeitos.</summary>
    public static Point Constrain(Mark s, Point p)
    {
        switch (s)
        {
            case ArrowMark a:
            {
                int dx = p.X - a.From.X, dy = p.Y - a.From.Y;
                if (Math.Abs(dx) > Math.Abs(dy) * 2) return new Point(p.X, a.From.Y);
                if (Math.Abs(dy) > Math.Abs(dx) * 2) return new Point(a.From.X, p.Y);
                var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
                return new Point(a.From.X + Math.Sign(dx) * m, a.From.Y + Math.Sign(dy) * m);
            }
            case BoxMark b:
            {
                int dx = p.X - b.A.X, dy = p.Y - b.A.Y;
                var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
                return new Point(b.A.X + Math.Sign(dx) * m, b.A.Y + Math.Sign(dy) * m);
            }
        }
        return p;
    }

    public static bool IsMeaningful(Mark s) => s switch
    {
        PenMark => true,
        ArrowMark a => Math.Abs(a.To.X - a.From.X) + Math.Abs(a.To.Y - a.From.Y) > 6,
        BoxMark b => Math.Abs(b.B.X - b.A.X) > 4 && Math.Abs(b.B.Y - b.A.Y) > 4,
        _ => true,
    };

    public static int NextNumber(IEnumerable<Mark> marks) =>
        marks.OfType<NumberMark>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
}
