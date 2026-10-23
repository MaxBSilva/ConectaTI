# ConectaTI — Gerenciador de Conexões para Suporte de TI

**Desenvolvido e mantido por [Max Silva](https://github.com/MaxBSilva).**

Aplicativo desktop para **Windows 11** desenvolvido em **C# (.NET 10 LTS)** com **WPF (arquitetura MVVM)** e banco de dados local **SQLite**, projetado para equipes de suporte de TI que organizam e acessam centenas ou milhares de servidores de diversos clientes via **Área de Trabalho Remota (RDP)**, **SSH (PuTTY e OpenSSH)**, **WinSCP (SFTP/SCP)** e **VNC**.

Leia [CONTEXTO.md](CONTEXTO.md) para entender como o app funciona, os fluxos de edição e senhas, a arquitetura e os dados portáteis. O [guia de desenvolvimento](DESENVOLVIMENTO.md) reúne as convenções de manutenção do projeto.

---

## 1. Pacote Executável Portátil (Pronto para Uso)

Uma versão **portátil e autossuficiente (Self-Contained Single-File para Windows x64)** já foi gerada e não exige privilégios administrativos nem instalação prévia do .NET:

- **Executável 1.2.1**: [`dist/win-x64-portatil-v1.2.1/ConectaTI.exe`](dist/win-x64-portatil-v1.2.1/ConectaTI.exe)
- **Pacote ZIP 1.2.1**: [`dist/ConectaTI-v1.2.1-win-x64-portatil.zip`](dist/ConectaTI-v1.2.1-win-x64-portatil.zip)
- **Correções e validações**: [RELATORIO-CORRECOES-1.2.1.md](RELATORIO-CORRECOES-1.2.1.md).

Os dados locais ficam ao lado do executável (ou na pasta personalizada já selecionada pelo usuário):
- `sqlite.db` — Banco SQLite portátil com migrações automáticas e modo WAL. Bases legadas continuam com a migração existente.
- `settings.json` — Preferências visuais, geometria de janela, caminhos de executáveis e parâmetros de segurança.
- `backups\` — Backups automáticos (criados antes de importações e restaurações) e backups manuais.
- `logs\` — Logs técnicos diários com sanitização automática de dados sensíveis.
- `temp\` — Arquivos `.rdp` de sessão sem senhas, limpos automaticamente.

---

## 2. Instruções para Executar, Compilar, Testar e Publicar

### Pré-requisitos para compilação
- **.NET 10 SDK** (`10.0.100` ou superior — instalado no perfil local em `%LOCALAPPDATA%\Microsoft\dotnet`).
- Sistema operacional Windows 10/11 x64.

### Executar em modo de desenvolvimento
```powershell
dotnet run --project src/ConectaTI.App/ConectaTI.App.csproj
```

### Compilar a solução
```powershell
dotnet build ConectaTI.slnx -c Release
```

### Executar a suíte de testes automatizados (xUnit)
```powershell
dotnet test ConectaTI.slnx
```

### Publicar versão portátil (Windows x64 Single-File)
```powershell
dotnet publish src/ConectaTI.App/ConectaTI.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o dist/win-x64-portatil-v1.2.1
```

---

## 3. Registro das Funcionalidades Concluídas

1. **Cadastro Completo e Persistência SQLite (`sqlite.db` ao lado do executável)**:
   - Identificador interno único (`GUID`) permitindo nomes semelhantes em clientes diferentes.
   - Campos: Nome, Cliente/Empresa, Grupo/Pasta, Ambiente (`Produção`, `Homologação`, `Teste`, `Outro`), Protocolo (`RDP`, `SSH`, `SFTP`, `SCP`, `VNC`), Hostname/IP (DNS, IPv4 e IPv6), Porta separada (validada entre `1` e `65535`), Usuário, Domínio, Credencial salva reutilizável, Caminho da chave privada, Tags, Observações e Favorito.
   - Registro automático da data/hora da última abertura (`LastOpenedAtUtc`).
   - Tratamento inteligente de identidade (`UserIdentityFormatter`): suporta usuários locais, `DOMINIO\usuario` e `usuario@dominio` (UPN), garantindo que o domínio **nunca** seja concatenado duas vezes.
   - Operações completas de **Novo, Editar, Salvar, Cancelar, Duplicar** (gerando novo identificador) e **Excluir**.

2. **Interface para Centenas/Milhares de Conexões e Usabilidade**:
   - Interface compacta com lista e formulário separados por uma divisória redimensionável (`GridSplitter`):
     - **Lista à esquerda**: Busca sempre visível e visões *Todos*, *Favoritos*, *Recentes* e *Lixeira*, com lista virtualizada de acessos.
     - **Painel Direito**: Edição manual com rascunhos em memória associados ao ID. Salvar ou Ctrl+S grava; navegar conserva o rascunho; **Descartar alterações** restaura os dados persistidos. O botão fica âmbar quando há alterações. Fechar descarta rascunhos sem confirmação.
   - **Atalhos de Teclado**:
     - `Enter` ou `Duplo Clique`: Conectar ao servidor selecionado.
     - `Ctrl+F`: Focar na caixa de pesquisa.
     - `Ctrl+N`: Novo cadastro.
     - `Ctrl+S`: Salvar durante a edição.
     - `Esc`: Limpar a busca ou a busca incremental, conforme o foco.
   - **Temas e Multi-monitor**:
     - Temas **Claro**, **Escuro** e **Conforme o Windows**.
     - Persistência de tamanho e posição da janela com validação de limites visíveis (`ClampWindowBounds`) para evitar janelas fora da tela após desconexão de monitores secundários.

3. **Abertura Segura dos Clientes Externos**:
   - Uso exclusivo de `ProcessStartInfo` com `UseShellExecute = false` e `ArgumentList` (tratando espaços, acentos e caracteres especiais sem concatenação em shell).
   - **RDP (`mstsc.exe` — Área de Trabalho Remota (MSTSC))**:
     - Protocolo padrão ao criar novas conexões (`porta 3389`), utilizando o caminho configurado em *Configurações → Executáveis* (`%WINDIR%\System32\mstsc.exe` por padrão, com suporte a caminho personalizado e restauração do padrão).
     - Gera arquivo `.rdp` temporário validado contra injeção de linhas (`\r`, `\n`), configurando endereço (`full address:s:`), porta, usuário, domínio, tela cheia ou resolução personalizada (`Width x Height`), múltiplos monitores (`use multimon:i:1`), sessão administrativa (`/admin`) e `authentication level:i:2`.
     - **Integração de Senha Salva com MSTSC**: Quando a conexão possui senha salva, provisiona a credencial de sessão (`TERMSRV/<host>` e `TERMSRV/<host>:<porta>`) via API nativa `CredWriteW` do Gerenciador de Credenciais do Windows (marcando apenas registros criados pelo ConectaTI para nunca substituir ou apagar credenciais preexistentes do Windows) e inclui o blob criptografado DPAPI (`password 51:b:` via `CryptProtectData` vinculado ao usuário atual do Windows). Quando a conexão não possui senha salva, limpa credenciais temporárias anteriores do ConectaTI para aquele host para não reutilizar usuário/senha indevidamente. Coordena múltiplas conexões para o mesmo servidor com usuários diferentes (em sequência ou simultaneamente).
   - **SSH (`putty.exe` e `ssh.exe` OpenSSH)**: Suporta porta customizada (`-P` / `-p`), usuário (`-l`), chave privada (`-i`) e agente de autenticação (`-agent` / `ForwardAgent`).
   - **WinSCP (`WinSCP.exe`)**: Suporta `SFTP` e `SCP` com porta, usuário e `/privatekey=...` sem expor senha na URL.
   - **VNC (`vncviewer.exe` / `tvnviewer.exe`)**: Suporta perfis de parâmetros para:
     - **TigerVNC / RealVNC**: `host::porta` (ou `[ipv6]::porta`)
     - **TightVNC**: `-host=host -port=porta`
     - **UltraVNC**: `host:porta`
     - **Template Personalizado**: Expansão segura de `{host}`, `{port}`, `{user}`.
   - **Feedback transparente**: Informa *"Cliente iniciado"* (não afirma que a sessão foi autenticada sem evidência do cliente externo).

4. **Cofre de Credenciais e Área de Transferência**:
   - Senhas nunca são salvas em texto puro no SQLite, configurações, arquivos `.rdp` ou logs.
   - Proteção combinando **DPAPI (`ProtectedData` vinculado ao usuário atual do Windows)** + **Gerenciador de Credenciais do Windows (`advapi32.dll`)**.
   - Compatibilidade com credenciais legadas protegidas por senha mestra, com migração para DPAPI. A versão atual não possui bloqueio ativo da aplicação.
   - Revelação temporária de senha com ocultação automática após intervalo configurável.
   - Cópia para área de transferência com retentativas automáticas (tratando `CLIPBRD_E_CANT_OPEN`) e limpeza automática verificada por hash `SHA-256` (limpa somente se o conteúdo da área de transferência ainda for a senha copiada pelo aplicativo).

5. **Importação, Exportação e Backups Consistentes**:
   - **Importação CSV e JSON** com suporte a UTF-8 e acentuação, **mapeamento interativo de colunas**, **prévia com validação linha a linha** e escolha do tratamento de duplicidades (*Ignorar*, *Atualizar* ou *Criar novo*).
   - **Avaliação de arquivos do AutoPuTTY**: Importa listas XML/INI não criptografadas do AutoPuTTY e detecta arquivos binários/criptografados proprietários, orientando o usuário com transparência.
   - **Exportação padrão CSV/JSON** sem senhas por padrão.
   - **Pacote Criptografado Portátil (`.ctienc`)**: Exporta conexões e credenciais criptografadas com senha própria de transporte (`PBKDF2-SHA256 + AES-256-GCM`) para migração segura entre computadores.
   - **Backups e Restauração**: Usa a API de Online Backup do SQLite (`BackupDatabase`), verifica integridade (`PRAGMA integrity_check`) antes de restaurar e faz backup automático antes de qualquer importação ou restauração.

6. **Diagnóstico de Conectividade (DNS + TCP)**:
   - Testa resolução DNS (IPv4/IPv6 e tempo em ms) e handshake TCP na porta configurada, com timeout e botão de cancelamento, diferenciando explicitamente *"porta TCP acessível"* de *"autenticação realizada"*.

---

## 4. Limitações Conhecidas e Decisões de Segurança

1. **Política contra passagem de senhas por linha de comando (`-pw` ou URLs)**:
   - Argumentos de linha de comando no Windows podem ser lidos por outros processos locais, ferramentas de monitoramento e logs de auditoria de criação de processo (Event ID 4688).
   - Por isso, o **ConectaTI nunca transmite senhas via `-pw` (PuTTY), URLs `sftp://user:senha@host` (WinSCP) ou parâmetros `-password` (VNC)**.
   - **Como funciona na prática**:
     - Para **Área de Trabalho Remota (`mstsc.exe`)**, o ConectaTI integra-se ao Gerenciador de Credenciais do Windows (`CredWriteW` em `TERMSRV/<host>`) e ao blob DPAPI de sessão (`password 51:b:`), sem utilizar `cmdkey` com senha em linha de comando, `SendKeys`, simulação de cliques ou área de transferência ao conectar.
     - Para **SSH, WinSCP e VNC**, recomenda-se o uso de chaves privadas SSH / Pageant (`ssh-agent`). Caso o analista deseje copiar a senha manualmente, há o botão explícito **Copiar Senha** no painel de detalhes (com limpeza automática verificada por hash SHA-256).

2. **Uso Local do SQLite (Não utilizar em pasta compartilhada de rede)**:
   - O arquivo `sqlite.db` fica ao lado do executável, salvo quando o usuário já escolheu uma pasta de dados personalizada.
   - O SQLite **não deve** ser colocado em pastas de rede (SMB/CIFS) para edição simultânea por múltiplos usuários, pois bloqueios de arquivo em rede podem causar corrupção de página ou `SQLITE_BUSY` sob concorrência.
   - Para compartilhar listas de servidores entre analistas atualmente, utilize a **Exportação/Importação CSV ou JSON** ou o **Pacote Criptografado (`.ctienc`)**.

3. **Arquivos Criptografados do AutoPuTTY**:
   - O ConectaTI importa listas abertas (XML, INI ou CSV) exportadas pelo AutoPuTTY. Caso o usuário selecione um banco do AutoPuTTY criptografado ou em formato binário proprietário, o aplicativo detecta e informa claramente que é necessário exportar a lista sem criptografia no AutoPuTTY antes de importar.

---

## 5. Evolução Futura Documentada (Compartilhamento Centralizado, Usuários e Permissões)

Para evoluir o **ConectaTI** de um repositório local por analista para uma arquitetura corporativa centralizada de equipe, recomenda-se a seguinte arquitetura em fases futuras:

1. **API Centralizada (`ConectaTI.Server` em ASP.NET Core 10 + PostgreSQL / SQL Server)**:
   - Substituir a sincronização manual por arquivos por uma API REST/gRPC interna autenticada via **Active Directory / Entra ID (OIDC / Kerberos)**.
   - O banco SQLite local do cliente desktop passa a atuar como **cache offline criptografado** (permitindo que o analista continue consultando servidores mesmo durante quedas de rede interna), sincronizando alterações via * optimistic concurrency* (`RowVersion` / `UpdatedAtUtc`).

2. **Controle de Acesso Baseado em Papéis (RBAC) por Cliente/Grupo**:
   - **Papéis sugeridos**:
     - `Administrador TI`: Gerencia usuários, políticas de bloqueio, clientes e cofres compartilhados.
     - `Analista N2/N3`: Cria/edita conexões e acessa credenciais dos grupos autorizados.
     - `Analista N1 / Plantão`: Permissão somente leitura dos cadastros e abertura de conexões nos clientes atribuídos.

3. **Cofre Corporativo e Auditoria Centralizada**:
   - As credenciais compartilhadas ficam criptografadas no servidor com chaves de envelope (KEK/DEK) ou integradas a um cofre corporativo (HashiCorp Vault / Azure Key Vault / CyberArk).
   - Registro central de auditoria (`AuditLogs`): quem abriu qual conexão ou revelou qual credencial, em qual horário e a partir de qual estação de trabalho.
