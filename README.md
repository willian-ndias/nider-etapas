# Nider Etapas

Manuais passo a passo, direto da sua tela. Grave o que você faz no computador e transforme em um
PDF com setas, círculos, textos e números. Para Windows, sem precisar de administrador.

## Instalar

1. Baixe `NiderEtapas-Windows.zip` na página de [Releases](https://github.com/willian-ndias/nider-etapas/releases).
2. Extraia, abra o `NiderEtapas.exe` e clique em **Instalar** (ou em **Usar sem instalar**).
3. Se o Windows avisar que o aplicativo não é reconhecido, clique em **Mais informações** e depois em
   **Executar assim mesmo**.

Mais detalhes em [LEIA-ME.txt](LEIA-ME.txt).

## Compilar

Precisa do .NET 8 SDK: `dotnet publish src/Trilha -c Release`. O GitHub também compila sozinho quando
uma etiqueta de versão (`v2.0.9`, por exemplo) é enviada: veja `.github/workflows/windows.yml`.

Autor: Willian Dias · [nider.com.br](https://nider.com.br)
