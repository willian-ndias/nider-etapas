using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Trilha;

/// <summary>Caixa de mensagem no estilo do app (substitui o MessageBox do Windows).</summary>
public partial class GlassDialog : Window
{
    int _result = -1;

    GlassDialog()
    {
        InitializeComponent();
        Shell.SizeChanged += (_, _) => Shell.Clip = new RectangleGeometry(new Rect(Shell.RenderSize), 22, 22);
        SourceInitialized += (_, _) => _ = new Frost(this, Shell, FrostImage, Glass.ExcludeFromCapture(this));
        Glass.MakeDraggable(this, Shell);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// Mostra a mensagem. O primeiro botão é o principal. Retorna o índice do botão clicado, ou -1 se fechou.
    /// </summary>
    public static int Show(Window? owner, string title, string message, params string[] buttons)
    {
        var d = new GlassDialog();
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        if (buttons.Length == 0) buttons = new[] { "OK" };
        // O principal fica à direita.
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)d.FindResource(i == 0 ? "PrimaryButton" : "SecondaryButton"),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 96,
                IsDefault = i == 0,
            };
            b.Click += (_, _) => { d._result = index; d.Close(); };
            d.Buttons.Children.Add(b);
        }
        if (owner != null && owner.IsVisible)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        d.ShowDialog();
        return d._result;
    }

    public static void Error(Window? owner, string message) =>
        Show(owner, "Algo deu errado", message + $"\n\nDetalhes em: {Log.PathFile}", "OK");
}
