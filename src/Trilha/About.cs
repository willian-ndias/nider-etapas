using System.Windows;

namespace Trilha;

/// <summary>Ficha do programa. O marcador do clique continua sendo só o formato do clique.</summary>
public static class About
{
    public static void Show(Window? owner)
    {
        GlassDialog.Show(owner, "Sobre o Nider Etapas",
            $"{AppInfo.Name}\nVersão {AppInfo.VersionText}\n\n{AppInfo.Publisher}\n{AppInfo.Author}\n{AppInfo.Site}\n{AppInfo.Email}",
            "Fechar");
    }
}
