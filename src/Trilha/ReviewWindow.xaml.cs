using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Trilha;

/// <summary>Um passo na lista da revisão.</summary>
public sealed class StepItem : INotifyPropertyChanged
{
    public Step Step { get; }
    int _number;
    BitmapSource? _thumb;

    public StepItem(Step step, int number) { Step = step; _number = number; }

    public int Number { get => _number; set { _number = value; Raise(nameof(Number)); Raise(nameof(Label)); } }
    public string Label => Step.IsTextBlock ? "TEXTO" : $"PASSO {_number}";
    public string Description => Step.IsTextBlock
        ? (string.IsNullOrWhiteSpace(Step.Description) ? FirstLine(Step.Comment, "Bloco de texto") : Step.Description)
        : Step.Description;

    static string FirstLine(string s, string fallback)
    {
        var l = (s ?? "").Replace("\r", "").Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim();
        return string.IsNullOrEmpty(l) ? fallback : l;
    }

    public BitmapSource Thumb
    {
        get
        {
            if (_thumb == null)
            {
                using var t = Step.Thumbnail(256);
                _thumb = Glass.ToBitmapSource(t);
            }
            return _thumb;
        }
    }

    public void RefreshText() => Raise(nameof(Description));
    public void RefreshThumb() { _thumb = null; Raise(nameof(Thumb)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Revisão depois de parar: ver, corrigir, desenhar, reordenar e salvar o PDF.</summary>
public partial class ReviewWindow : Window
{
    readonly List<Step> _steps;
    readonly ObservableCollection<StepItem> _items = new();
    bool _loading;

    public bool StartNew { get; private set; }
    public bool Saved { get; private set; }

    public ReviewWindow(List<Step> steps)
    {
        InitializeComponent();
        _steps = steps;
        Title = $"{AppInfo.Name} · revisar manual";
        AppName.Text = AppInfo.Name;
        TitleBox.Text = $"Manual passo a passo - {DateTime.Now:dd/MM/yyyy}";
        foreach (var st in steps) _items.Add(new StepItem(st, 0));
        List.ItemsSource = _items;
        Renumber();
        SourceInitialized += (_, _) => Glass.DarkChrome(this);
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Loaded += (_, _) => { if (_items.Count > 0) List.SelectedIndex = 0; UpdateCount(); };
    }

    StepItem? Current => List.SelectedItem as StepItem;

    void UpdateCount()
    {
        int steps = _items.Count(i => !i.Step.IsTextBlock), texts = _items.Count - steps;
        CountText.Text = (steps == 1 ? "1 PASSO" : $"{steps} PASSOS") + (texts == 0 ? "" : texts == 1 ? " · 1 TEXTO" : $" · {texts} TEXTOS");
    }

    void Renumber()
    {
        _steps.Clear();
        int n = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (!_items[i].Step.IsTextBlock) n++;
            _items[i].Number = n;
            _steps.Add(_items[i].Step);
        }
        UpdateCount();
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    void ShowSelected()
    {
        _loading = true;
        var it = Current;
        if (it == null)
        {
            Preview.Source = null;
            DescriptionBox.Text = CommentBox.Text = "";
        }
        else if (it.Step.IsTextBlock)
        {
            Preview.Source = null;
            TextTitleBox.Text = it.Step.Description;
            TextBodyBox.Text = it.Step.Comment;
        }
        else
        {
            using var bmp = it.Step.Render();
            Preview.Source = Glass.ToBitmapSource(bmp);
            DescriptionBox.Text = it.Step.Description;
            CommentBox.Text = it.Step.Comment;
        }
        var isText = it?.Step.IsTextBlock == true;
        TextPanel.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        ImageCard.Visibility = StepFields.Visibility = isText ? Visibility.Collapsed : Visibility.Visible;
        DescriptionBox.IsEnabled = CommentBox.IsEnabled = DrawButton.IsEnabled = CropButton.IsEnabled = MarkerButton.IsEnabled = PhotoButton.IsEnabled = it != null && !isText;
        _loading = false;
    }

    void Description_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || Current is not StepItem it) return;
        it.Step.Description = DescriptionBox.Text;
        it.RefreshText();
    }

    void TextTitle_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || Current is not StepItem it) return;
        it.Step.Description = TextTitleBox.Text;
        it.RefreshText();
    }

    void TextBody_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || Current is not StepItem it) return;
        it.Step.Comment = TextBodyBox.Text;
        it.RefreshText();
    }

    /// <summary>Insere um bloco de texto depois do passo selecionado (ou no fim).</summary>
    void AddText_Click(object sender, RoutedEventArgs e)
    {
        var at = List.SelectedIndex < 0 ? _items.Count : List.SelectedIndex + 1;
        _items.Insert(at, new StepItem(Step.NewTextBlock(), 0));
        Renumber();
        List.SelectedIndex = at;
        List.ScrollIntoView(List.SelectedItem);
        Dispatcher.BeginInvoke(() => TextTitleBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void Comment_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || Current is not StepItem it) return;
        it.Step.Comment = CommentBox.Text;
    }

    void EditDrawing()
    {
        if (Current is not StepItem it || it.Step.IsTextBlock) return;
        var monitor = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var w = new AnnotateWindow(it.Step, overlay: false, monitor, it.Number) { Owner = this };
        if (w.ShowDialog() == true)
        {
            it.RefreshThumb();
            it.RefreshText();
            ShowSelected();
        }
    }

    void Draw_Click(object sender, RoutedEventArgs e) => EditDrawing();

    /// <summary>Troca o formato/cor do destaque do clique: só neste passo ou em todos (e nas próximas gravações).</summary>
    void Marker_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not StepItem it || it.Step.IsTextBlock) return;
        var start = it.Step.Marks.OfType<ClickMark>().FirstOrDefault()?.Style ?? ClickStyle.Current;
        var w = new MarkerWindow(start, it.Step) { Owner = this };
        if (w.ShowDialog() != true) return;
        var targets = w.Choice == MarkerWindow.Result.ThisStep ? new[] { it } : _items.ToArray();
        if (w.Choice == MarkerWindow.Result.AllSteps) ClickStyle.Current = w.Picked;
        foreach (var t in targets)
        {
            var clicks = t.Step.Marks.OfType<ClickMark>().ToList();
            foreach (var c in clicks) c.Style = w.Picked.Copy();
            if (clicks.Count > 0) t.RefreshThumb();
        }
        ShowSelected();
    }

    /// <summary>Recorta o print do passo. Dá para recortar de novo depois: o print inteiro fica guardado.</summary>
    void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not StepItem it || it.Step.IsTextBlock) return;
        try
        {
            var w = new CropWindow(it.Step, it.Number) { Owner = this };
            if (w.ShowDialog() != true) return;
            it.Step.Crop(w.Selection);
            it.Step.BorderSize = w.BorderSize;
            it.Step.BorderColor = w.BorderColor;
            it.RefreshThumb();
            ShowSelected();
        }
        catch (Exception ex)
        {
            Log.Error("Recortar", ex);
            GlassDialog.Error(this, "Não consegui ajustar a imagem: " + ex.Message);
        }
    }

    /// <summary>Troca a imagem do passo: um arquivo do computador ou um print novo da tela.</summary>
    async void Photo_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not StepItem it || it.Step.IsTextBlock) return;
        var warn = it.Step.Marks.Count > 0 ? " Os desenhos feitos nesta imagem serão apagados." : "";
        var choice = GlassDialog.Show(this, "Trocar imagem do passo " + it.Number,
            "Escolha de onde vem a nova imagem." + warn, "Escolher arquivo", "Tirar print agora", "Cancelar");
        System.Drawing.Bitmap? bmp = null;
        try
        {
            if (choice == 0)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Escolher imagem",
                    Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos|*.*",
                };
                if (dlg.ShowDialog(this) != true) return;
                using var loaded = new System.Drawing.Bitmap(dlg.FileName);
                bmp = new System.Drawing.Bitmap(loaded); // solta o arquivo
            }
            else if (choice == 1)
            {
                // Esconder (Hide) fecharia esta janela; então ela só minimiza durante o print.
                var state = WindowState;
                WindowState = WindowState.Minimized;
                await Task.Delay(700);
                bmp = Imaging.Capture(System.Windows.Forms.Screen.PrimaryScreen!.Bounds);
                WindowState = state;
                Activate();
            }
            else return;

            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            it.Step.ReplaceImage(ms.ToArray());
            if (choice == 1) it.Step.TrimTaskbar(System.Windows.Forms.Screen.PrimaryScreen!.Bounds);
            it.RefreshThumb();
            ShowSelected();
        }
        catch (Exception ex)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Log.Error("TrocarImagem", ex);
            GlassDialog.Error(this, "Não consegui usar essa imagem: " + ex.Message);
        }
        finally { bmp?.Dispose(); }
    }
    void Preview_Click(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) EditDrawing(); }
    void List_DoubleClick(object sender, MouseButtonEventArgs e) => EditDrawing();

    void Move(int delta)
    {
        var i = List.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        Renumber();
        List.SelectedIndex = j;
        List.ScrollIntoView(List.SelectedItem);
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(+1);

    void DeleteSelected()
    {
        var i = List.SelectedIndex;
        if (i < 0) return;
        _items.RemoveAt(i);
        Renumber();
        if (_items.Count > 0) List.SelectedIndex = Math.Min(i, _items.Count - 1);
        else ShowSelected();
    }

    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }

    // ------------------------------------------------------------ salvar

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_items.Any(i => !i.Step.IsTextBlock))
        {
            GlassDialog.Show(this, "Não há passos", "Grave alguns passos antes de salvar o manual.", "OK");
            return;
        }
        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Manual passo a passo" : TitleBox.Text.Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Arquivo PDF (*.pdf)|*.pdf",
            FileName = SafeFileName(title) + ".pdf",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Title = "Salvar manual em PDF",
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;

        Busy.Visibility = Visibility.Visible;
        BusyBar.Value = 0;
        try
        {
            var steps = _items.Select(i => i.Step).ToList();
            var progress = new Progress<double>(v => BusyBar.Value = v);
            await Task.Run(() =>
            {
                var report = (IProgress<double>)progress;
                var pdfSteps = new List<PdfStep>();
                int n = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    if (steps[i].IsTextBlock)
                        pdfSteps.Add(new PdfStep(0, steps[i].Description, steps[i].Comment, Array.Empty<byte>(), IsText: true));
                    else
                        pdfSteps.Add(new PdfStep(++n, steps[i].Description, steps[i].Comment, steps[i].RenderJpeg()));
                    report.Report(80.0 * (i + 1) / steps.Count);
                }
                PdfExporter.Export(path, title, pdfSteps, DateTime.Now);
                report.Report(100);
            });
            Saved = true;
        }
        catch (Exception ex)
        {
            Log.Error("PDF", ex);
            Busy.Visibility = Visibility.Collapsed;
            GlassDialog.Error(this, "Não consegui salvar o PDF: " + ex.Message);
            return;
        }
        Busy.Visibility = Visibility.Collapsed;

        var r = GlassDialog.Show(this, "Manual salvo!", Path.GetFileName(path), "Abrir PDF", "Abrir pasta", "Fechar");
        try
        {
            if (r == 0) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (r == 1) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s.Length > 80 ? s[..80] : s;
    }

    // ------------------------------------------------------------ rodapé e janela

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!Saved && GlassDialog.Show(this, "Descartar este manual?", "Todos os passos gravados serão apagados.", "Descartar", "Cancelar") != 0) return;
        StartNew = true;
        Close();
    }

    void Continue_Click(object sender, RoutedEventArgs e) => Close();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
