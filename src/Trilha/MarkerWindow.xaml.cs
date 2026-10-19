using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;

namespace Trilha;

/// <summary>Escolhe como o clique aparece no print: formato, cor e contorno no botão. Mostra o resultado na hora.</summary>
public partial class MarkerWindow : Window
{
    public enum Result { None, ThisStep, AllSteps }

    public ClickStyle Picked { get; private set; }
    public Result Choice { get; private set; } = Result.None;

    readonly Step _sample;
    readonly bool _forStep;
    bool _ready;

    /// <param name="step">Passo da revisão para usar de exemplo; null = imagem de exemplo (aberto pela barra).</param>
    public MarkerWindow(ClickStyle start, Step? step = null)
    {
        InitializeComponent();
        Picked = start.Copy();
        _forStep = step != null;
        _sample = step != null && step.Marks.OfType<ClickMark>().Any() ? step : SampleStep();

        foreach (var (shape, name) in ClickStyle.Shapes)
        {
            var r = new RadioButton { GroupName = "shape", Content = name, Tag = shape, Margin = new Thickness(1),
                                      Style = (System.Windows.Style)FindResource("Chip"), IsChecked = shape == Picked.Shape };
            r.Checked += Option_Changed;
            ShapeChips.Children.Add(r);
        }
        var picked = false;
        foreach (var (name, c) in ClickStyle.Colors)
        {
            var same = c.ToArgb() == Picked.Color.ToArgb();
            picked |= same;
            var r = new RadioButton { GroupName = "color", ToolTip = name, Tag = c, IsChecked = same,
                                      Style = (System.Windows.Style)FindResource("Swatch"),
                                      Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) };
            r.Checked += Option_Changed;
            ColorChips.Children.Add(r);
        }
        if (!picked) ((RadioButton)ColorChips.Children[0]).IsChecked = true;
        (Picked.Outline ? OutlineOn : OutlineOff).IsChecked = true;

        if (_forStep)
        {
            // "Só neste passo" só faz sentido se este passo tem um clique marcado.
            if (step!.Marks.OfType<ClickMark>().Any()) ThisButton.Visibility = Visibility.Visible;
            AllText.Text = "Em todos os passos";
            FooterHint.Text = "\"Em todos\" também vale para as próximas gravações.";
        }

        SourceInitialized += (_, _) => Glass.DarkChrome(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        _ready = true;
        Option_Changed(this, new RoutedEventArgs());
    }

    void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        foreach (RadioButton r in ShapeChips.Children) if (r.IsChecked == true) Picked.Shape = (ClickShape)r.Tag;
        foreach (RadioButton r in ColorChips.Children) if (r.IsChecked == true) Picked.Color = (SD.Color)r.Tag;
        Picked.Outline = OutlineOn.IsChecked == true;
        var backdrop = Picked.Shape is ClickShape.Spotlight or ClickShape.Blur;
        OutlineOn.IsEnabled = OutlineOff.IsEnabled = !backdrop;
        ShapeHint.Text = Picked.Shape switch
        {
            ClickShape.Spotlight => "Holofote: escurece o resto da tela e deixa só o botão clicado aceso.",
            ClickShape.Blur => "Desfoque: borra o resto da tela e deixa só o botão clicado nítido.",
            ClickShape.Arrow => "Seta: aponta para o lugar do clique. Perto da borda ela vira sozinha.",
            _ => "O contorno aparece quando o Windows informa onde fica o botão clicado.",
        };
        ShowPreview();
    }

    void ShowPreview()
    {
        var clicks = _sample.Marks.OfType<ClickMark>().ToList();
        var old = clicks.Select(c => c.Style).ToList();
        foreach (var c in clicks) c.Style = Picked.Copy();
        try
        {
            using var bmp = _sample.Render();
            Preview.Source = Glass.ToBitmapSource(bmp);
        }
        catch (Exception ex) { Log.Error("Marcador", ex); }
        finally { for (int i = 0; i < clicks.Count; i++) clicks[i].Style = old[i]; }
    }

    /// <summary>Uma janela de mentira com um botão "Salvar" clicado, para ver o marcador quando ainda não há passos.</summary>
    static Step SampleStep()
    {
        const int W = 880, H = 520;
        using var bmp = new SD.Bitmap(W, H);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
            g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(SD.Color.FromArgb(226, 230, 235));
            using var white = new SD.SolidBrush(SD.Color.White);
            using var bar = new SD.SolidBrush(SD.Color.FromArgb(243, 245, 247));
            using var line = new SD.SolidBrush(SD.Color.FromArgb(210, 215, 221));
            using var ink = new SD.SolidBrush(SD.Color.FromArgb(40, 45, 52));
            using var soft = new SD.SolidBrush(SD.Color.FromArgb(120, 126, 136));
            using var blue = new SD.SolidBrush(SD.Color.FromArgb(0, 103, 192));
            g.FillRectangle(white, 40, 30, W - 80, H - 60);
            g.FillRectangle(bar, 40, 30, W - 80, 44);
            using var title = new SD.Font("Segoe UI", 15, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
            using var body = new SD.Font("Segoe UI", 14, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
            g.DrawString("Cadastro de usuário", title, ink, 62, 42);
            string[] labels = { "Nome", "E-mail", "Senha" };
            for (int i = 0; i < 3; i++)
            {
                var y = 110 + i * 82;
                g.DrawString(labels[i], body, soft, 70, y);
                g.FillRectangle(line, 70, y + 24, 420, 38);
                g.FillRectangle(white, 71, y + 25, 418, 36);
            }
            g.FillRectangle(blue, 70, 372, 132, 42);
            using var onBlue = new SD.SolidBrush(SD.Color.White);
            g.DrawString("Salvar", title, onBlue, 110, 383);
            g.FillRectangle(line, 214, 372, 120, 42);
            g.FillRectangle(white, 215, 373, 118, 40);
            g.DrawString("Cancelar", body, ink, 244, 384);
            for (int i = 0; i < 6; i++) g.FillRectangle(line, 560, 112 + i * 34, i % 2 == 0 ? 230 : 180, 12);
        }
        var s = Step.FromBitmap(bmp);
        s.Marks.Add(new ClickMark { Center = new SD.Point(150, 394), Element = new SD.Rectangle(70, 372, 132, 42) });
        return s;
    }

    void This_Click(object sender, RoutedEventArgs e) { Choice = Result.ThisStep; DialogResult = true; }
    void All_Click(object sender, RoutedEventArgs e) { Choice = Result.AllSteps; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
