using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SD = System.Drawing;

namespace Trilha;

/// <summary>Ferramentas de desenho, cores e espessuras, ligadas a uma AnnotationSurface.</summary>
public partial class ToolPalette : UserControl
{
    AnnotationSurface? _surface;

    // Lembra a última escolha entre um desenho e outro.
    static Tool _lastTool = Tool.Arrow;
    static int _lastColor;
    static int _lastSize = 1;
    static readonly float[] Widths = { 3, 5, 9 };

    public ToolPalette()
    {
        InitializeComponent();
    }

    public void Attach(AnnotationSurface surface)
    {
        _surface = surface;
        foreach (RadioButton r in Tools.Children)
        {
            var tool = Enum.Parse<Tool>((string)r.Tag);
            r.IsChecked = tool == _lastTool;
            r.Checked += (_, _) => { surface.CommitText(); surface.Tool = _lastTool = tool; };
        }
        for (int i = 0; i < Colors.Children.Count; i++)
        {
            var r = (RadioButton)Colors.Children[i];
            var index = i;
            var c = ((SolidColorBrush)r.Background).Color;
            r.IsChecked = i == _lastColor;
            r.Checked += (_, _) => { surface.DrawColor = SD.Color.FromArgb(c.A, c.R, c.G, c.B); _lastColor = index; };
        }
        for (int i = 0; i < Sizes.Children.Count; i++)
        {
            var r = (RadioButton)Sizes.Children[i];
            var index = i;
            r.Tag = double.Parse((string)r.Tag, System.Globalization.CultureInfo.InvariantCulture);
            r.IsChecked = i == _lastSize;
            r.Checked += (_, _) => { surface.DrawWidth = Widths[index]; _lastSize = index; };
        }
        surface.Tool = _lastTool;
        var col = ((SolidColorBrush)((RadioButton)Colors.Children[_lastColor]).Background).Color;
        surface.DrawColor = SD.Color.FromArgb(col.A, col.R, col.G, col.B);
        surface.DrawWidth = Widths[_lastSize];
    }

    void Undo_Click(object sender, RoutedEventArgs e) => _surface?.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => _surface?.Redo();
    void Clear_Click(object sender, RoutedEventArgs e) => _surface?.ClearAll();
}
