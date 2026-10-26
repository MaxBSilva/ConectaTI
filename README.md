# ConectaTI

Gerenciador de acessos remotos para equipes de suporte que atendem diversos clientes todos os dias.

**Desenvolvido por [Max Silva](https://github.com/MaxBSilva), com auxílio de inteligência artificial e revisão no ambiente interno de trabalho.**

O ConectaTI reúne os acessos em uma interface para Windows e inicia os clientes de conexão já utilizados pela equipe. Cada instalação mantém sua própria base local e portátil.

## Recursos

- Conexões RDP, SSH, SFTP, SCP e VNC.
- Organização por cliente, grupo e ambiente, com busca, favoritos e acessos recentes.
- Cadastro com salvamento manual e rascunhos em memória durante o uso.
- Proteção de senhas com DPAPI do usuário Windows.
- Importação de cadastros, incluindo formatos compatíveis com AutoPuTTY, e edição em lote.
- Backup local e transferência de acessos com opção de senhas em pacote criptografado.
- Interface em português, inglês e espanhol.
- Sessões RDP identificadas pelo nome do acesso, com arquivos temporários separados por abertura.

## Utilização

1. Abra o aplicativo em uma pasta com permissão de escrita.
2. Cadastre o nome do acesso, endereço, protocolo e demais informações necessárias.
3. Use **Salvar** ou **Ctrl+S** para gravar o cadastro.
4. Use **Conectar**, Enter ou duplo clique no acesso para iniciar o cliente remoto.

O aplicativo utiliza MSTSC, PuTTY/OpenSSH, WinSCP ou o visualizador VNC configurado. Esses clientes precisam estar disponíveis no computador. O endereço aceita IP ou hostname e porta personalizada.

Navegar entre acessos e conectar não salva alterações do formulário. Os rascunhos permanecem em memória até o aplicativo ser fechado.

## Dados e credenciais

Por padrão, `sqlite.db`, `settings.json`, backups e logs ficam na pasta do aplicativo. Ao atualizar uma instalação, feche o ConectaTI e substitua somente os arquivos do programa, preservando os dados locais.

As senhas protegidas por DPAPI pertencem ao usuário Windows de origem. Para transferir acessos com senhas para outro perfil, use a exportação criptografada do aplicativo. Copiar o banco não converte essa proteção.

O projeto foi pensado para apoiar várias equipes por meio de instalações locais. A base SQLite não oferece sincronização entre equipes nem edição simultânea em uma base compartilhada.

O repositório contém código-fonte e exemplos fictícios. Bases, configurações locais, credenciais, logs e arquivos de distribuição ficam fora do Git.

## Desenvolvimento

Versão atual: **1.5.0**. Projeto em **C# / .NET 10**, com **WPF / MVVM** e **SQLite**, destinado ao Windows x64.

Com o SDK .NET 10 instalado no Windows:

```powershell
dotnet build ConectaTI.slnx -c Release
dotnet test ConectaTI.slnx -c Release
```

Para abrir o aplicativo em um ambiente isolado de teste:

```powershell
dotnet run --project tools/ConectaTI.UiSmoke -c Release
```

O iniciador de teste utiliza uma pasta própria e evita a descoberta de bases pessoais. Também é possível escolher uma pasta de dados explicitamente:

```powershell
.\ConectaTI.exe --data-dir "C:\Teste-ConectaTI"
```

Para gerar uma publicação portátil em pasta separada:

```powershell
dotnet publish src/ConectaTI.App/ConectaTI.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o dist/win-x64-portatil
```

Testes automatizados verificam o comportamento local do aplicativo. Autenticação nos servidores, políticas de rede, múltiplos monitores e comportamento dos clientes externos dependem do ambiente de utilização. O MSTSC mantém seu sufixo nativo no título e pode mostrar somente o destino na barra de tela cheia.

## Autor

[Max Silva — MaxBSilva](https://github.com/MaxBSilva)
