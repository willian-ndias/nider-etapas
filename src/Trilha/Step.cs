using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Trilha;

/// <summary>Um passo do manual: o print, os desenhos por cima, a descrição e o comentário.</summary>
public sealed class Step
{
    /// <summary>Print original, guardado em PNG para não ocupar muita memória.</summary>
    public byte[] ScreenshotPng = Array.Empty<byte>();
    public readonly List<Mark> Marks = new();
    public string Description = "";
    public string Comment = "";
    public DateTime Time = DateTime.Now;
    /// <summary>Bloco só de texto (título + parágrafos), sem print. Não conta como passo numerado.</summary>
    public bool IsTextBlock;

    public static Step NewTextBlock() => new() { IsTextBlock = true, Description = "", Comment = "" };

    /// <summary>Print inteiro de antes do recorte (null = nunca recortado). Permite recortar de novo sem perder nada.</summary>
    public byte[]? FullPng;
    /// <summary>Onde o recorte atual começa dentro do print inteiro.</summary>
    public Point CropOrigin;

    // Para juntar dois cliques rápidos num "clique duplo".
    internal Point? ClickPoint;
    internal DateTime ClickTime;

    public static Step FromBitmap(Bitmap shot)
    {
        using var ms = new MemoryStream();
        shot.Save(ms, ImageFormat.Png);
        return new Step { ScreenshotPng = ms.ToArray() };
    }

    public Bitmap LoadScreenshot()
    {
        using var ms = new MemoryStream(ScreenshotPng);
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    /// <summary>Borda em volta do print: 0 = sem, 1 = fina, 2 = média, 3 = grossa.</summary>
    public int BorderSize;
    public Color BorderColor = Color.FromArgb(29, 34, 39);

    public static int BorderPixels(int size, int imageWidth) =>
        size <= 0 ? 0 : Math.Max(2 * size, (int)Math.Round(imageWidth * new[] { 0, 0.004, 0.009, 0.016 }[Math.Min(size, 3)]));

    /// <summary>Print com todos os desenhos e a borda.</summary>
    public Bitmap Render()
    {
        var plain = RenderPlain();
        var b = BorderPixels(BorderSize, plain.Width);
        if (b == 0) return plain;
        var framed = new Bitmap(plain.Width + 2 * b, plain.Height + 2 * b);
        using (var g = Graphics.FromImage(framed))
        {
            g.Clear(BorderColor);
            g.DrawImage(plain, b, b, plain.Width, plain.Height);
        }
        plain.Dispose();
        return framed;
    }

    /// <summary>Print com todos os desenhos, sem borda.</summary>
    public Bitmap RenderPlain()
    {
        var bmp = LoadScreenshot();
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        MarkRender.DrawAll(g, bmp, Marks);
        return bmp;
    }

    /// <summary>Print inteiro (sem recorte) com os desenhos na posição certa.</summary>
    public Bitmap RenderFull()
    {
        if (FullPng == null) return RenderPlain();
        using var ms = new MemoryStream(FullPng);
        using var img = Image.FromStream(ms);
        var bmp = new Bitmap(img);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.TranslateTransform(CropOrigin.X, CropOrigin.Y);
        MarkRender.DrawAll(g, bmp, Marks);
        return bmp;
    }

    public Size FullSize()
    {
        using var ms = new MemoryStream(FullPng ?? ScreenshotPng);
        using var img = Image.FromStream(ms);
        return img.Size;
    }

    /// <summary>Recorta o print. <paramref name="area"/> é medido no print inteiro.</summary>
    public void Crop(Rectangle area)
    {
        FullPng ??= ScreenshotPng;
        using var ms = new MemoryStream(FullPng);
        using var full = new Bitmap(Image.FromStream(ms));
        area.Intersect(new Rectangle(Point.Empty, full.Size));
        if (area.Width < 2 || area.Height < 2) return;
        using var part = full.Clone(area, full.PixelFormat);
        using var outMs = new MemoryStream();
        part.Save(outMs, ImageFormat.Png);
        int dx = CropOrigin.X - area.X, dy = CropOrigin.Y - area.Y;
        foreach (var m in Marks) m.Move(dx, dy);
        CropOrigin = area.Location;
        if (area == new Rectangle(Point.Empty, full.Size)) { ScreenshotPng = FullPng; FullPng = null; }
        else ScreenshotPng = outMs.ToArray();
    }

    /// <summary>
    /// Tira a barra de tarefas do Windows do print (recorte normal: dá para voltar em "Usar imagem inteira").
    /// <paramref name="monitor"/> é a área da tela de onde o print veio.
    /// </summary>
    public void TrimTaskbar(Rectangle monitor)
    {
        try
        {
            var wa = Screen.FromRectangle(monitor).WorkingArea;
            wa.Intersect(monitor);
            if (wa == monitor || wa.Width < 100 || wa.Height < 100) return;
            wa.Offset(-monitor.X, -monitor.Y);
            Crop(wa);
        }
        catch { /* sem recorte, o print fica inteiro */ }
    }

    /// <summary>Descobre de qual tela veio um print pelo tamanho (preferindo a tela do mouse).</summary>
    public static Rectangle MonitorFor(Bitmap shot)
    {
        var here = Screen.FromPoint(Cursor.Position).Bounds;
        if (here.Size == shot.Size) return here;
        foreach (var sc in Screen.AllScreens) if (sc.Bounds.Size == shot.Size) return sc.Bounds;
        return Rectangle.Empty;
    }

    /// <summary>Imagem nova: esquece recorte e desenhos.</summary>
    public void ReplaceImage(byte[] png)
    {
        ScreenshotPng = png; FullPng = null; CropOrigin = Point.Empty; Marks.Clear();
    }

    /// <summary>Imagem final do passo em PNG (sem perda): JPEG borra e serrilha texto de tela.</summary>
    public byte[] RenderPng()
    {
        using var bmp = Render();
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Miniatura de um bloco de texto: um cartão claro com linhas, como uma página.</summary>
    Bitmap TextThumbnail(int width)
    {
        var h = width * 9 / 16;
        var t = new Bitmap(width, h);
        using var g = Graphics.FromImage(t);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(242, 244, 246));
        using var title = new SolidBrush(Color.FromArgb(29, 34, 39));
        using var line = new SolidBrush(Color.FromArgb(180, 187, 196));
        int x = width / 10, y = h / 6, lw = width - 2 * x, lh = Math.Max(3, h / 16);
        g.FillRectangle(title, x, y, lw * 6 / 10, lh * 2);
        y += lh * 4;
        for (int i = 0; i < 4 && y + lh < h - h / 8; i++, y += lh * 2) g.FillRectangle(line, x, y, i == 3 ? lw * 6 / 10 : lw, lh);
        return t;
    }

    public Bitmap Thumbnail(int width)
    {
        if (IsTextBlock) return TextThumbnail(width);
        using var full = Render();
        var h = Math.Max(1, full.Height * width / full.Width);
        var t = new Bitmap(width, h);
        using var g = Graphics.FromImage(t);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(full, 0, 0, width, h);
        return t;
    }
}

internal static class Imaging
{
    /// <summary>Tira um print de uma área da tela (em pixels físicos).</summary>
    public static Bitmap Capture(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
