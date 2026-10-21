using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Drawing;

namespace Trilha;

public sealed record PdfStep(int Number, string Description, string Comment, byte[] Image, bool IsText = false);

/// <summary>Gera o manual passo a passo em PDF.</summary>
public static class PdfExporter
{
    const string Accent = "#1D2227";
    const string Accent2 = "#8F969E";
    const float Margin = 42.52f;                          // 1,5 cm
    static readonly float ContentWidth = PageSizes.A4.Width - 2 * Margin - 2;
    const float MaxImageHeight = 440;                     // ~15,5 cm

    static PdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
        // A fonte vai dentro do .exe: o PDF sai igual em qualquer Windows.
        var asm = typeof(PdfExporter).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var font = asm.GetManifestResourceStream(name);
            if (font != null) FontManager.RegisterFont(font);
        }
        QuestPDF.Settings.EnableDebugging = Environment.GetEnvironmentVariable("ETAPAS_DEBUG") == "1";
    }

    public static void Export(string path, string title, IReadOnlyList<PdfStep> steps, DateTime created)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(Margin);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontSize(11).FontFamily("Lato", "Segoe UI", "Arial").FontColor("#202020"));

                page.Header().PaddingBottom(12).Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(title).FontSize(21).Bold().FontColor("#1D2227");
                            c.Item().PaddingTop(2).Text($"Criado em {created:dd/MM/yyyy 'às' HH:mm}  ·  {steps.Count(x => !x.IsText)} {(steps.Count(x => !x.IsText) == 1 ? "passo" : "passos")}")
                                .FontSize(9.5f).FontColor("#6B7083");
                        });
                        var logo = LogoSvg();
                        if (logo != null)
                            row.AutoItem().AlignTop().Width(22).Height(22).Svg(logo);
                        row.AutoItem().AlignTop().PaddingTop(5).PaddingLeft(5).Text(AppInfo.Name).FontSize(9).FontColor("#C3C8CF");
                    });
                    col.Item().PaddingTop(8).Row(r =>
                    {
                        r.RelativeItem(3).Height(3).Background(Accent);
                        r.RelativeItem(2).Height(3).Background(Accent2);
                    });
                });

                page.Content().Column(col =>
                {
                    col.Spacing(18);
                    foreach (var s in steps)
                    {
                        if (s.IsText)
                        {
                            // Bloco de texto livre: título opcional e parágrafos.
                            col.Item().Column(tb =>
                            {
                                tb.Spacing(6);
                                if (!string.IsNullOrWhiteSpace(s.Description))
                                    tb.Item().Text(s.Description.Trim()).FontSize(14).Bold().FontColor(Accent);
                                foreach (var para in Paragraphs(s.Comment))
                                    tb.Item().Text(para).FontSize(11).LineHeight(1.35f);
                            });
                            continue;
                        }
                        // Passo inteiro (título, imagem e observação) fica na mesma página.
                        // Só textos enormes podem quebrar, senão não caberiam em página nenhuma.
                        var compact = s.Description.Length + s.Comment.Length < 900;
                        var block = compact ? col.Item().ShowEntire() : col.Item();
                        block.Column(top =>
                        {
                            top.Spacing(6);
                            top.Item().Row(row =>
                            {
                                row.AutoItem().Background(Accent).PaddingVertical(3).PaddingHorizontal(8)
                                    .Text($"PASSO {s.Number}").FontSize(9.5f).Bold().FontColor(Colors.White);
                                row.RelativeItem().PaddingLeft(10).AlignMiddle()
                                    .Text(s.Description).FontSize(12).SemiBold();
                            });
                            // Tamanho fixo: se não couber no fim da página, o passo vai para a próxima.
                            var (w, h) = FitImage(s.Image, ContentWidth - 36, MaxImageHeight);
                            top.Item().AlignCenter().Width(w).Height(h)
                                .Border(0.75f).BorderColor("#D9DCE6")
                                .Image(s.Image).FitArea();

                            if (!string.IsNullOrWhiteSpace(s.Comment))
                            {
                                top.Item().PaddingTop(2).Background("#F1F3F5").BorderLeft(3).BorderColor(Accent2)
                                    .Padding(8).Text(text =>
                                    {
                                        text.Span("Observação: ").Bold().FontColor(Accent);
                                        text.Span(s.Comment);
                                    });
                            }
                        });
                    }
                });

                page.Footer().PaddingTop(8).AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(t => t.FontSize(8).FontColor("#C3C8CF"));
                    text.Span($"Feito com {AppInfo.Name}  ·  Página ");
                    text.CurrentPageNumber();
                    text.Span(" de ");
                    text.TotalPages();
                });
            });
        });

        // Prints de tela têm texto e bordas finas: 300 dpi e compressão máxima, senão serrilha.
        doc.WithSettings(new DocumentSettings { ImageRasterDpi = 300, ImageCompressionQuality = ImageCompressionQuality.Best })
           .WithMetadata(new DocumentMetadata { Title = title, Creator = $"{AppInfo.Name} · {AppInfo.Site}", Producer = $"{AppInfo.Name} · {AppInfo.Site}" })
           .GeneratePdf(path);
    }

    /// <summary>Cada Enter vira um parágrafo novo.</summary>
    static IEnumerable<string> Paragraphs(string text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0);

    static (float w, float h) FitImage(byte[] image, float maxW, float maxH)
    {
        var (pw, ph) = PngSize(image);
        if (pw <= 0 || ph <= 0) (pw, ph) = JpegSize(image);
        if (pw <= 0 || ph <= 0) return (maxW, maxW * 9 / 16);
        var ratio = (float)pw / ph;
        var w = Math.Min(maxW, maxH * ratio);
        return (w, w / ratio);
    }

    /// <summary>Lê largura e altura do cabeçalho IHDR do PNG.</summary>
    static (int w, int h) PngSize(byte[] d)
    {
        if (d.Length < 24 || d[0] != 0x89 || d[1] != 0x50 || d[2] != 0x4E || d[3] != 0x47) return (0, 0);
        int Be(int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
        return (Be(16), Be(20));
    }

    /// <summary>Lê largura e altura direto do cabeçalho do JPEG.</summary>
    static (int w, int h) JpegSize(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            var len = (d[i + 2] << 8) | d[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
            i += 2 + len;
        }
        return (0, 0);
    }

    static string? _logo;
    /// <summary>Logo em vetor (SVG): nítida em qualquer zoom do PDF.</summary>
    static string? LogoSvg()
    {
        if (_logo != null) return _logo;
        try
        {
            using var st = typeof(PdfExporter).Assembly.GetManifestResourceStream("logo.svg");
            if (st == null) return null;
            using var rd = new StreamReader(st);
            return _logo = rd.ReadToEnd();
        }
        catch { return null; }
    }
}
