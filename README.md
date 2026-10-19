# Nider Etapas

Manuais passo a passo, direto da sua tela. Grave o que você faz no computador e transforme em um
PDF com setas, círculos, textos e números. Para Windows, sem precisar de administrador.

## Instalar

1. Baixe `nider-etapas-setup-…-windows-x64.exe` na aba Downloads do Nider ou na página de
   [Releases](https://github.com/willian-ndias/nider-etapas/releases).
2. Abra o arquivo e clique em **Instalar** (ou em **Usar sem instalar**).
3. Se o Windows avisar que o aplicativo não é reconhecido, clique em **Mais informações** e depois em
   **Executar assim mesmo**.

Depois de instalado, o próprio programa avisa ao abrir quando sai versão nova e se atualiza
sozinho (consulta `nider.com.br/api/apps/nider-etapas/versao`; precisa da versão 2.0.12 ou mais
nova — as anteriores têm de ser atualizadas à mão uma vez).

Mais detalhes em [LEIA-ME.txt](LEIA-ME.txt).

## Compilar

Precisa do .NET 8 SDK: `dotnet publish src/Trilha -c Release`. O GitHub também compila sozinho quando
uma etiqueta de versão (`v2.0.9`, por exemplo) é enviada: veja `.github/workflows/windows.yml`.

Autor: Willian Dias · [nider.com.br](https://nider.com.br)
