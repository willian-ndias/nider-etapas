using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Trilha;

/// <summary>Tela de abertura: o hexágono "carregando", o nome e quem fez. Some sozinha (ou com um clique).</summary>
public partial class SplashWindow : Window
{
    static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1600);
    bool _closing;

    public SplashWindow()
    {
        InitializeComponent();
        ByText.Text = $"by {AppInfo.Site}";
        ContactText.Text = $"{AppInfo.Author}  ·  {AppInfo.Email}  ·  v{AppInfo.VersionText}";
        SourceInitialized += (_, _) => _ = new Frost(this, Shell, FrostImage, Glass.ExcludeFromCapture(this));
        MouseLeftButtonDown += (_, _) => FadeOut();
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter or Key.Space) FadeOut(); };
        Loaded += (_, _) =>
        {
            Outer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            // Perímetro do hexágono = 420 px = 140 traços de 3 px.
            Tracer.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(0, -140, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
            var bar = new DoubleAnimation(0, 1, Duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
            bar.Completed += (_, _) => FadeOut();
            Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, bar);
        };
    }

    void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(Outer.Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Close();
        Outer.BeginAnimation(OpacityProperty, fade);
    }
}
