# ConectaTI

Versão **2.1.0**. O painel **Textos rápidos** exibe a tecla de ativação configurada (**Scroll Lock** por padrão) e usa a mesma versão do executável `ConectaTI.exe`. Consulte [CONTEXTO.md](CONTEXTO.md) e [ARQUITETURA.md](ARQUITETURA.md).

Em **Configurações → Geral** ou no painel **Textos rápidos**, use **Buscar atualizações** para consultar a Release estável em `MaxBSilva/ConectaTI`. As Releases usam os anexos `ConectaTI-<versão>-win-x64.zip` e `release-manifest.json` (gerados automaticamente por `tools/Publish-Release.ps1` e pelo workflow `.github/workflows/release.yml`). O ZIP contém apenas `ConectaTI.exe`.

## Novidades da 2.1.0

- **Abrir até 3 sites no navegador padrão ao iniciar o Windows**: Em **Configurações → Geral → Inicialização com o Windows e sites automáticos**, configure até 3 endereços (`Site 1`, `Site 2`, `Site 3`), ative/desative a inicialização junto ao Windows (`--windows-startup`) e use **Abrir sites agora** para testar imediatamente. Endereços sem esquema recebem `https://` automaticamente; apenas URLs `http://` e `https://` são aceitas.
- **Alterar o botão padrão (`Scroll Lock`) de Textos rápidos**: No rodapé do painel lateral **Textos rápidos** ou em **Configurações → Atalhos**, clique no campo da tecla de abertura para definir outra tecla dedicada (`Pause`, `Insert`, `F1–F12`, `Num Lock`) ou combinação com `Ctrl`/`Alt` (`Ctrl+Espaço`, `Ctrl+Shift+Q`, etc.), com botão **Padrão** para restaurar `Scroll Lock`. A escolha fica salva em `%APPDATA%\HotKeyText\textos.xml` (`AtalhoAtivacao`).
- **Copiar dados de acesso e senha em Gerenciar credenciais salvas**: Em **Configurações → Credenciais → Gerenciar credenciais salvas** (ou menu de contexto da lista de credenciais), use **Copiar dados de acesso** para copiar nome, usuário/domínio formatado, senha salva recuperada do cofre (ou substituta digitada) e observações, além dos botões rápidos **📋 (Copiar usuário)** e **Copiar Senha**, respeitando a limpeza automática da área de transferência e proteção contra troca de seleção.
- **Atalhos da janela principal personalizáveis**: Em **Configurações → Atalhos**, os atalhos atuais (`Ctrl+N`, `Ctrl+S`, `Ctrl+F`, `F2`, `Ctrl+,` e `F1`) permanecem como padrão, mas cada ação pode ser alterada clicando na caixa do atalho e pressionando uma nova combinação (com verificação de conflito, reserva de `Ctrl+C`/`V`/`X`/`A`/`Z` e botões **Padrão** / **Restaurar padrões**).
- **Dois botões de conexão (`Conectar` + `Conectar VPN`), busca automática de perfil e clique direto em 1 clique por família de VPN (homologação)**: Quando o cliente possui VPN configurada, o botão **Conectar VPN** aparece diretamente ao lado do botão principal **Conectar** (além de dentro do bloco **VPN do cliente**, do menu `•••` e do menu de contexto da lista). No bloco **VPN do cliente**, os campos **Usuário da VPN** e **Senha da VPN** são manuais e opcionais (com máscara, botão `👁` revelar temporário, `📋` copiar, remoção explícita e gravação exclusiva por `Salvar`/`Ctrl+S`, migrações SQLite 11 e 12 com backup prévio). **Busca inteligente e conexão em 1 clique**: mesmo quando `Nome da VPN` não foi digitado manualmente, o ConectaTI localiza o túnel já configurado no PC cruzando `Nome da VPN` → `Servidor da VPN` → `Cliente` / `Nome da conexão` → `Usuário configurado` no perfil local (desambiguando túneis do mesmo servidor sem chutar perfis ambíguos). **Regra de não interferência**: se **Usuário da VPN** e **Senha da VPN** ficarem em branco — ou se o cliente VPN já estiver com `[x] Salvar Senha` marcado e usuário preenchido — o ConectaTI preserva as credenciais salvas e não toca na área de transferência; mesmo quando informados, a automação só preenche campos vazios. Cada cliente é tratado conforme sua mecânica real no Windows: **FortiClient** aciona `FortiClientConsole.exe --force-renderer-accessibility` (nunca `FortiTray.exe`), desperta a árvore Chromium/Electron (`Chrome_RenderWidgetHostHWND` via `WM_GETOBJECT` + `OBJID_CLIENT`), verifica se já está conectado, seleciona `Nome da VPN` ignorando `Certificado do Cliente`, preserva `[x] Salvar Senha` e aciona **Conectar** com confirmação de transição (quando o FortiClient roda como Administrador `🛡️`, usa ponte sob demanda `--vpn-uia-helper` via UAC/Named Pipe sem expor segredos em CLI, além da opção **Mais ▾ → Executar como Administrador** que preserva a inicialização `HKCU\Run`); **OpenVPN GUI** usa `--command connect <perfil>` na bandeja ou `--connect <perfil.ovpn>` fechado; **OpenVPN Connect v3** usa `--list-profiles` e `--connect-shortcut=<id>`; **OpenConnect GUI** restaura a janela Qt, seleciona o perfil e clica em `Connect`; **Cisco Secure Client / AnyConnect** autentica via pipe em memória `vpncli.exe -s`; **SonicWall NetExtender** usa `NECLI.exe`; **VPN do Windows** tenta conexão direta com credencial salva via `rasdial.exe "<perfil>"` com fallback para `rasphone.exe -d "<perfil>"`; e redes mesh (**Radmin VPN**, **WireGuard**, **Tailscale**, **ZeroTier**, **Hamachi**) restauram/abrem a interface gráfica. Ao clicar em **Conectar** sem a VPN ligada, se o servidor não responder em 1,2s, exibe o aviso inteligente com **Conectar VPN**, **Conectar mesmo assim** ou **Cancelar**.
- **CI e publicação automática em nuvem (`.github/workflows/release.yml`)**: Compila e executa os testes em `windows-latest` a cada push/PR na `main` (ou branches `feat/**` gerando artefato de prévia) e publica automaticamente a GitHub Release (`ConectaTI-<versão>-win-x64.zip` + `release-manifest.json`) ao enviar uma tag `v*.*.*` ou acionar manualmente o workflow.

## Novidades da 1.8.1 (HotKeyText integrado em executável único, Tema, Engrenagem e Barra Central)

- **Textos rápidos integrado dentro do ConectaTI (`ConectaTI.exe` único)**: O módulo de textos rápidos (baseado no `HotKeyText` v1.3.0, commit `7b5791b39e59c96a4203ed3a71ad7e73f8f125ac` de `viniciusdaveigaa/HotKeyText`, crédito a **Vinícius da Veiga**) roda integralmente dentro do próprio processo do `ConectaTI.exe`, sem `HotKeyText.exe` avulso ou extraído.
- **Aba na borda direita e painel recolhível Textos rápidos**: Use a aba **Textos rápidos** na borda direita da janela (ou o botão **Textos rápidos** na barra superior / menu **Mais ▾**) para abrir e recolher o painel lateral. Em janela larga (`>= 900px`), a lista, os detalhes e o painel **Textos rápidos** ficam lado a lado; em janela estreita (`< 900px`), o painel abre sobre os detalhes da conexão mantendo a lista acessível. Recolher o painel apenas oculta a interface, restaurando o foco e o estado do editor enquanto o `Scroll Lock` e os atalhos continuam ativos até o fechamento do ConectaTI.
- **Gerenciamento completo, `%APPDATA%\HotKeyText\textos.xml`, conflito e janela de destino**: Dentro do painel, cadastre (`+ Novo`), edite (`Título`, `Atalho`, `Texto a inserir`), exclua (`Excluir`), ordene (`▲` / `▼`), configure atalhos (com botão `Padrão`), escolha entre **Digitar** ou **Colar**, marque/desmarque **Exibir texto** (ocultando a prévia com `••••••••`) e escolha **Iniciar com o Windows**. Se a edição avulsa do `HotKeyText` estiver em execução, o ConectaTI identifica o conflito e orienta a encerrá-la antes de ativar os atalhos integrados. O texto é inserido na janela externa de destino correta mesmo depois de abrir o painel para gerenciar textos.
- **Tema, Configurações e barra central**:
  - **Tema**: O botão, o menu `Mais ▾`, a aba de Configurações e as dicas passaram a usar **Tema** (preservando o ícone sol/lua e as preferências já salvas).
  - **Configurações**: O botão **Configurações** usa uma engrenagem vetorial claramente reconhecível (8 dentes definidos e círculo central vazado), nítida no tamanho real do botão nos temas claro e escuro, com a dica **Configurações**.
  - **Barra de rolagem e divisor central**: As barras de rolagem usam indicador fino, cinza e com pontas arredondadas, trilho discreto e maior contraste ao passar o mouse, preservando roda, teclado e arraste. O divisor central entre os painéis mantém área confortável para redimensionar com alça vertical discreta.
- **Buscar atualizações pelo GitHub (`MaxBSilva/ConectaTI`) e migração para executável único**: Em **Configurações → Geral**, clique em **Buscar atualizações** para consultar a última Release estável em `MaxBSilva/ConectaTI`. Valida autenticidade e integridade (SHA-256 e cabeçalho PE), avisa sobre rascunhos pendentes antes de reiniciar, migra automaticamente instalações anteriores de dois executáveis (`1.8.0`) para a entrega de executável único (`1.8.1`), preserva conexões, credenciais, configurações, backups, textos e salvamento explícito (`Salvar`/`Ctrl+S`), e mantém reversão automática/manual (`Restaurar versão anterior` ou `--rollback-update`) em caso de falha.
- **Filtro de situação Todos / Ativos / Inativos**: No painel esquerdo, filtre rapidamente por **Todos** (padrão), **Ativos** ou **Inativos**, em combinação com a caixa de pesquisa e com as visões **Todos**, **Favoritos**, **Recentes** e **Lixeira**. Mantém a seleção quando o item permanece visível, ajusta foco e rolagem quando necessário, move um resultado visível por vez nas setas `↑/↓`, preserva rascunhos em memória e atualiza a lista imediatamente ao salvar (`Salvar`/`Ctrl+S`) uma mudança de situação.

## Evolução visual, Aparência e atalhos (1.8.0)

- **Barra superior adaptativa e diferenciação clara**: O botão **Aparência** (ícone vetorial sol/lua) alterna entre tema Escuro e Claro, separado do botão **⚙ Configurações**. Em janelas estreitas, as ações secundárias (`Importar RDP`, `Editar em lote` e `Aparência`) são agrupadas automaticamente no menu **Mais ▾**, sem barra de rolagem horizontal e mantendo os painéis esquerdo e direito sempre lado a lado.
- **Configurações organizadas em 7 abas**: **Geral** (idioma e tempo de limpeza da área de transferência), **Aparência** (tema Escuro/Claro, densidade Compacto/Confortável e tamanho da fonte 11–18 independentes), **Programas** (clientes externos com validação de caminho visível), **Credenciais**, **Importação e backup**, **Atalhos** e **Sobre e diagnóstico**. Cada seção informa explicitamente como sua alteração é aplicada.
- **Atalhos padronizados e navegação previsível**: `Ctrl+N` (nova conexão), `Ctrl+S` (salvar explicitamente), `Ctrl+F` (focar busca), `F2` (focar nome da conexão), `Ctrl+,` (abrir Configurações), `F1` (consultar atalhos), `Enter` na lista (conectar), `↑/↓` (mover um item visível por vez) e `Esc` contextual (fecha menus ou limpa a busca apenas quando o foco está na busca/lista, sem apagar o filtro nem o rascunho durante a edição de campos).
- **Acessibilidade e estados sem depender apenas de cor**: Foco de teclado visível em todos os controles interativos, indicador textual + ícone para estado da senha (`✓ Senha salva`, `● Senha alterada — salvar`, `⚠ Remoção pendente — salvar`, `○ Sem senha salva`), selo de pendências (`● Alterações não salvas`), estrela `★` em favoritos e identificação tripla para acessos inativos (faixa lateral, nome em vermelho e selo `Inativo`).

## Acessos ativos e inativos

Marque/desmarque **Ativo/Inativo** ao lado do nome e use **Salvar / Ctrl+S**. Inativos continuam visíveis e permitem pesquisa, edição, cópia e conexão manual. A lista mostra nome vermelho, faixa e selo **Inativo**, preservando PROD/TESTE. O status é independente da lixeira e do ambiente. A migração 10 considera os registros antigos ativos e faz backup prévio; os formatos próprios de backup/exportação preservam o status. Veja `RELATORIO-ATIVO-INATIVO.md`.

## Identificação das sessões RDP

Arquivos de abertura agora usam **Nome cadastrado - Endereço꞉porta.rdp**, em uma subpasta exclusiva por abertura. O identificador aleatório fica fora do nome exibido. Acentos são preservados; `꞉` substitui o `:` proibido em nomes Windows, sem alterar a porta real. O sufixo do título e a identificação em tela inteira são controlados pelo MSTSC. A validação do título/barra de tarefas após autenticação ainda depende de uma sessão autorizada disponível; consulte `RELATORIO-IDENTIFICACAO-RDP.md`.

## Busca por digitação e navegação na lista

Com o foco na lista, digite parte do nome sem precisar de acentos. Após localizar, ↑/↓ percorrem exatamente uma linha na ordem exibida, sem circular nos limites. O clique em outro acesso passa a ser a nova referência. Shift/Ctrl continuam disponíveis para seleção múltipla. Digitar no campo Search ou nos detalhes mantém o comportamento desses campos. Ver `RELATORIO-NAVEGACAO-LISTA.md` para reprodução e testes de 30/10/2026.

## Cópia completa e tamanho das letras

O botão **Copiar todos os dados**, abaixo de Conectar/Salvar, copia o formulário atual, incluindo alterações não salvas, senha disponível, Gateway e VPN. Não é necessário expandir seções. A área de transferência é limpa após o intervalo configurado, se ainda contiver a mesma cópia.

Em **Configurações → Aparência**, ajuste **Tamanho da fonte (11–18)** e a **Densidade da interface (Compacto / Confortável)** de forma independente. A preferência é pré-visualizada imediatamente e confirmada ao salvar. Use **Restaurar tamanho padrão (12,5)** para voltar. Os painéis continuam lado a lado e há rolagem quando necessário. Executável atualizado em `dist/ConectaTI-1.8.0-win-x64/ConectaTI.exe`. Ver `RELATORIO-EVOLUCAO-1.8.0.md`.

**Desenvolvido e mantido por [Max Silva](https://github.com/MaxBSilva).**

Gerenciador de acessos remotos para Windows x64, em C#/.NET 10, WPF/MVVM e SQLite portátil. Organiza clientes, grupos, ambientes e credenciais e abre MSTSC, PuTTY/OpenSSH, WinSCP ou VNC. Funciona localmente e offline; os clientes remotos dependem da rede e dos programas configurados.

[RELATORIO-EVOLUCAO-1.8.0.md](RELATORIO-EVOLUCAO-1.8.0.md) registra o diagnóstico, o comparativo visual e os testes da 1.8.0. As entregas anteriores estão documentadas em [RELATORIO-REFATORACAO-1.7.0.md](RELATORIO-REFATORACAO-1.7.0.md) e [RELATORIO-ENTREGA-1.6.0.md](RELATORIO-ENTREGA-1.6.0.md).

## Executar e testar

O script `tools/Publish-Release.ps1` gera a distribuição em `dist/`, acompanhada de `release-manifest.json` com versão e SHA-256. O executável é autossuficiente para Windows x64 e pode ser executado sem instalar o .NET ou usar uma conta de administrador. `dist/` e `artifacts/` ficam fora do Git.

Para um primeiro teste sem descobrir bases pessoais, use `--data-dir` com uma pasta temporária nova. A publicação não contém banco, configurações, logs, backups ou credenciais.

Também é possível escolher uma pasta explicitamente:

```powershell
.\ConectaTI.exe --data-dir "C:\Teste-ConectaTI"
```

Com `--data-dir`, o app usa somente essa pasta e não executa a descoberta de bases legadas. Sem o argumento, mantém a política existente: pasta personalizada salva pelo usuário ou pasta do executável, com migração compatível de bases legadas.

Para atualizar uma instalação, feche o app, mantenha o backup e substitua somente o executável. Preserve `sqlite.db`, `settings.json`, `backups/`, `logs/` e o ponteiro de pasta personalizada. O pacote não contém dados nem credenciais do usuário.

## Uso

- **Ctrl+N** cria um rascunho. **Salvar / Ctrl+S** grava o cadastro. Navegar mantém os rascunhos da sessão e não salva nem pergunta se deseja salvar. Fechar descarta os rascunhos.
- Digitar com o foco na lista busca por nome; nos campos do formulário, continua editando o campo. **Ctrl+F** foca a busca, **F2** foca o nome da conexão, **Ctrl+,** abre Configurações e **F1** abre a consulta de atalhos. Enter ou duplo clique na lista conecta.
- Use Ctrl/Shift na lista para selecionar vários acessos e **Editar em lote**. Cliente e grupo permitem Não alterar, Definir valor e Limpar campo. Ambiente permite Produção, Homologação e Teste. O lote preserva os demais campos e credenciais.
- O menu de contexto copia o endereço real, sem porta, ou o usuário real. Ações vazias ficam desabilitadas. O modelo não tem um campo IP separado; nenhuma resolução DNS é feita para copiar dados.
- Em **Configurações → Geral**, escolha o idioma (Automático, Português (Brasil), English ou Español) e o tempo de limpeza da área de transferência. A troca de idioma é imediata e não modifica filtros, seleção, dados ou rascunhos.
- Em **Configurações → Aparência** (ou pelo botão **Aparência** na barra superior), escolha tema Escuro/Claro, densidade Compacto/Confortável e fonte de 11 a 18 de forma independente. A divisória e as larguras das colunas da prévia são lembradas e ajustadas dinamicamente para manter ambos os painéis visíveis.
- A senha tem ícone, texto e cor: `✓ Senha salva` (verde), `○ Sem senha salva` (vermelho), `● Senha alterada — salvar` e `⚠ Remoção pendente — salvar` (âmbar). Falha de recuperação é um estado próprio. Campo protegido vazio mantém a credencial; remoção é explícita e exige Salvar.

## Importação e backups

Abra **Configurações → Importação e backup**. Selecione XML AutoPuTTY, XML ConectaTI, CSV, JSON ou INI compatível. O pacote criptografado `.ctienc` também passa por prévia. Revise a seleção e escolha ignorar, atualizar ou duplicar antes de confirmar. Atualizar conserva IDs; duplicar gera novos IDs; senha ausente não elimina a anterior.

Leitura, decodificação e gravação usam tarefas fora da interface, progresso por etapa e cancelamento antes do commit. Conexões e credenciais são confirmadas em uma transação SQLite. Os segredos usam DPAPI CurrentUser no mesmo SQLite. A confirmação não grava cópias no Credential Manager. Após o commit, o resultado é concluído, mesmo que um cancelamento chegue depois. O resumo informa encontrados, importados, atualizados, ignorados, duplicados e erros; detalhes copiados não incluem XML completo ou senhas.

Limites: arquivo de 32 MiB, 20.000 acessos, campo de 262.144 caracteres, até 256 colunas CSV, XML com profundidade até 64 e estrutura limitada. DTD e entidades externas são proibidos. O cancelamento de uma derivação Argon2 em execução é atendido ao finalizar essa derivação. Não há processamento paralelo de vários registros criptografados.

Exportações CSV/JSON/XML comuns não levam senhas. A seleção também se aplica a esses formatos.

## Proteção automática e transferência

Novas senhas são protegidas automaticamente por DPAPI CurrentUser e armazenadas no SQLite, no formato versionado CTDPAPI2. No mesmo usuário Windows, conectar, revelar e copiar funcionam pelas ações existentes, sem criar ou desbloquear senha mestra. O campo fica mascarado por padrão. A identificação pública do usuário e a versão não são uma chave de criptografia.

Ao copiar a pasta para outro usuário, os acessos continuam visíveis. As senhas com identificação de origem incompatível mostram: **Senha protegida por outro usuário. Importe um backup com senhas ou informe uma nova senha.** Elas não são reveladas, copiadas ou preenchidas pelo app; o cliente remoto pode solicitar autenticação manual. Novos cadastros e substituições explícitas continuam disponíveis, sem apagar os segredos copiados. Falhas técnicas ou dados corrompidos têm mensagem distinta. Formatos DPAPI antigos sem identificação de origem não permitem atribuir uma falha a outro usuário com certeza; o diagnóstico conserva essa distinção.

Na central existente, abra **Exportar**:

1. Exporte todos ou ajuste os checkboxes dos acessos escolhidos. Busca e filtros por cliente, grupo, ambiente e nome preservam a seleção. Use **Selecionar resultados filtrados**, **Limpar seleção**, contador e **Revisar somente selecionados**.
2. Marque **Incluir senhas** se necessário. Informe a senha do arquivo com pelo menos oito caracteres e confirme-a.
3. **Exportar seleção** gera .ctienc com senhas ou .ctishare somente com cadastros. Credenciais indisponíveis impedem a geração com senhas; um arquivo anterior não é sobrescrito nessa situação.
4. O pacote inclui somente os acessos selecionados fora da lixeira e, quando solicitado, as credenciais vinculadas a eles, uma vez por credencial compartilhada. Não inclui banco, credenciais avulsas nem chave global.
5. No destino, use **Importar pacote de transferência**, informe a senha do arquivo quando houver, revise a prévia e a política de duplicidade e confirme. Segredos são protegidos novamente com DPAPI do usuário importador; os vínculos são preservados no commit transacional.

O transporte .ctienc usa AES-256-GCM e PBKDF2-HMAC-SHA256 com 600.000 iterações, salt aleatório de 32 bytes e parâmetros versionados/autenticados em CTIENC02. Ele independe de DPAPI para abrir em outro perfil. A senha de exportação não vai para settings e não existem arquivos temporários com senhas em texto puro. Leitura de CTIENC01 e de pacotes anteriores é mantida.

**Backup local do SQLite** mantém a proteção do usuário de origem. Restaurar o banco não recriptografa senhas para outro perfil. Para transferir senhas, gere explicitamente um pacote de transferência com senha; o app não o gera automaticamente.

Bases com o cofre da versão 1.4.0 ou outros formatos com senha mestra solicitam a senha anterior uma única vez para a migração criptográfica. A conversão preserva IDs, compartilhamento, metadados e versões anteriores protegidas, e verifica a leitura antes do commit. Senha errada, falha ou cancelamento preservam os originais. É possível tentar novamente em **Configurações → Migrar senhas do formato anterior**, exibido quando há registros pendentes. Depois da conversão, o uso diário é automático. Credenciais Windows legadas continuam legíveis quando disponíveis; os originais Windows não são apagados automaticamente.

RDP é uma integração separada. MSTSC pode usar credenciais TERMSRV temporárias e o campo DPAPI do arquivo .rdp; uma falha nessa preparação não interfere em importar/salvar. Login automático RDP não foi validado em servidor remoto.

## Diagnóstico

**Copiar relatório de diagnóstico** inclui versão, arquitetura, Windows/runtime, idiomas, caminho do banco, etapa/data/tipo/mensagem/stack da falha e contexto disponível de importação. Working Set é a memória do processo no momento da coleta, não a memória da falha anterior nem um pico. Falhas de CredWriteW preservam imediatamente o código Win32, a etapa, a linha de origem e a posição da credencial, sem registrar o segredo. Logs têm limite de 512 KiB por arquivo, rotação e retenção de até 12 arquivos.

## Desenvolvimento

Use Windows e SDK .NET 10. O SDK usado nesta entrega está em `%LOCALAPPDATA%\ConectaTI-Development\dotnet\dotnet.exe`.

```powershell
dotnet build ConectaTI.slnx -c Release
dotnet test ConectaTI.slnx -c Release
dotnet run --project tools/ConectaTI.UiSmoke -c Release
```

O iniciador UiSmoke usa somente dados fictícios. Não execute testes com a base pessoal. Para registrar as métricas sintéticas em JSONL, defina `CONECTATI_IMPORT_METRICS` com um caminho de arquivo antes de executar os testes.

```powershell
dotnet publish src/ConectaTI.App/ConectaTI.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o dist/ConectaTI-1.8.0-win-x64
```

Os logs do incidente confirmaram CredWriteW/Win32 8 na gravação adicional, agora retirada das importações e salvamentos. A razão interna do erro nativo não foi estabelecida e o XML original ainda precisa de confirmação no ambiente do incidente. Escalas reais do Windows e múltiplos monitores devem ser conferidos no equipamento de uso.
# RD Gateway e importação RDP (29/10/2026)

Use **Importar RDP** na barra superior, revise o novo rascunho e clique em **Salvar**. A importação não substitui cadastros. UTF-8 e UTF-16 com BOM são reconhecidos; o sufixo literal /admin é separado do endereço com aviso.

Configure manualmente em **Mais opções → RD Gateway**: servidor, utilização, origem de credenciais, perfil e reutilização. Para o caso informado, preserve **2 / 4 / 1** e mesmas credenciais habilitado. Não é necessário salvar senha.

Em **RDP avançado e diagnóstico**, edite propriedades adicionais e visualize/exporte a configuração sem segredos. Campos estruturados prevalecem; a cópia original é sanitizada e a saída não é assinada. O Gateway foi introduzido na migração 9; a versão atual usa esquema 10, com backup prévio nas migrações. JSON, XML próprio, CSV próprio e pacotes preservam as novas opções.

Executável Windows x64 com runtime: `dist/ConectaTI-1.7.0-win-x64/ConectaTI.exe`. Consulte `RELATORIO-RDP-GATEWAY.md` para arquivos, testes e roteiro de validação real.


## Correções da versão 1.6.0

Operações de senha capturam sessão, geração, identidade e revisão; respostas antigas, falhas e tentativas de clipboard são descartadas após navegação, edição ou fechamento. Somente Salvar/Ctrl+S grava cadastros. Os painéis permanecem lado a lado.

Propriedades RDP de segurança usam inteiros normalizados: grafias como `00`, `+0` e espaços recebem a mesma validação de `0`. Tipo incorreto e valores fora do domínio são rejeitados na leitura, gravação e geração. As regras existentes continuam proibindo desativar CredSSP ou negociação de segurança e autenticação de nível zero. Valores válidos de gateway são preservados.

XML/CSV próprios preservam `SshClient`, `SshUseAgent` e `VncParameters`, além das opções RDP/Gateway, VPN, favoritos e status. XML aceita também o nome legado `SshPreference`. Campo ausente usa cliente `Default`, agente habilitado e parâmetros VNC vazios. XML/CSV não transportam senhas nem vínculos locais de credencial; para transportar credenciais use o pacote criptografado próprio. JSON e pacotes mantêm as opções do modelo.

Publicação reproduzível (compila, testa, publica e confere uma pasta nova):

```powershell
.\tools\Publish-Release.ps1 -Dotnet "$env:LOCALAPPDATA\ConectaTI-Development\dotnet\dotnet.exe"
```

O script recusa uma pasta de versão já existente e não copia dados da instalação anterior. Não execute o aplicativo publicado sem `--data-dir` durante uma verificação isolada.

## Estrutura da versão 1.7.0

Sessões/revisões, regras da lista, validade assíncrona, leitura/cópia de credenciais, migrações, codecs próprios e planos/execução foram separados em componentes testáveis. O esquema permanece 10 e os nomes serializados anteriores continuam compatíveis. Diagnósticos distinguem “fora da lixeira”, status manual Ativo/Inativo e disponibilidade TCP.

Foi reproduzido e corrigido um cenário adicional: uma conclusão atrasada do diagnóstico de A não pode sobrescrever o painel de B. As verificações principais da janela podem ser repetidas com dados fictícios:

```powershell
dotnet run --project tools/ConectaTI.UiSmoke -c Release -- --refactor-checks --narrow --font=18 --dark
dotnet run --project tools/ConectaTI.UiSmoke -c Release -- --navigation --navigation-checks --narrow --font=18 --dark
```

O modo de refatoração usa uma pasta temporária nova e um clipboard substituído para não copiar os segredos fictícios para a área de transferência do usuário. O resultado é registrado em `tools/ConectaTI.UiSmoke/bin/Release/net10.0-windows/refactor-checks.json`. Não alterou a descoberta de dados do aplicativo normal.
