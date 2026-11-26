# Contexto do ConectaTI

## Versão unificada e atualização estável — versão 2.0.0 (05/11/2026)

- A versão do projeto WPF alimenta o assembly, o cabeçalho localizado de **Textos rápidos** e os dois componentes no manifesto. A entrega continua em um único `ConectaTI.exe`. O módulo integrado usa como referência o HotKeyText v1.3.0 de **Vinícius da Veiga** (`viniciusdaveigaa/HotKeyText`, commit `7b5791b39e59c96a4203ed3a71ad7e73f8f125ac`).
- **Buscar atualizações** em Configurações e em Textos rápidos, `--check-updates` e o evento externo abrem a mesma consulta à última Release estável de `MaxBSilva/ConectaTI`. Os anexos devem apontar para downloads das Releases desse repositório.
- O pacote `ConectaTI-2.0.0-win-x64.zip` e o `release-manifest.json` mantêm o contrato da 1.8.1, incluindo o nome e o hash do executável em ambos os componentes, a origem oficial, a atribuição do módulo e a validação do ZIP e do binário. A substituição preserva o ponto de recuperação da instalação anterior.

## Integração in-process do HotKeyText (executável único), Tema, Engrenagem e Barra Central — versão 1.8.1 (05/11/2026)

- **HotKeyText integrado dentro do processo do ConectaTI (`ConectaTI.exe` único)**: O módulo de textos rápidos (`HotKeyTextService` + `QuickTextsPopupWindow`, baseado no commit `7b5791b39e59c96a4203ed3a71ad7e73f8f125ac` da `v1.3.0` de `viniciusdaveigaa/HotKeyText`, mantendo o crédito a **Vinícius da Veiga**) passa a rodar integralmente dentro do próprio processo `ConectaTI.exe`. A entrega de publicação possui **um único executável (`ConectaTI.exe`)**, sem distribuir ou extrair `HotKeyText.exe` avulso.
- **Aba lateral direita e painel recolhível Textos rápidos**: Na borda direita da janela principal (`MainWindow`), a aba **Textos rápidos** (`QuickTextsEdgeTabButton`) e o botão superior **Textos rápidos** abrem e recolhem o painel lateral (`QuickTextsSidePanel`). Em janela larga (`>= 900px`), a lista, os detalhes da conexão e o painel **Textos rápidos** permanecem lado a lado. Em janela estreita (`< 900px`), o painel abre sobre os detalhes da conexão (`Grid.Column="2"`) mantendo a lista de conexões (`Grid.Column="0"`) visível e acessível. Ao recolher o painel, apenas a interface é ocultada, restaurando o foco de teclado e preservando qualquer rascunho de edição em andamento; o `Scroll Lock` e os atalhos continuam ativos enquanto o ConectaTI estiver aberto, liberando o hook de teclado (`WH_KEYBOARD_LL`) ao fechar.
- **Gerenciamento completo, `textos.xml`, detecção de conflito e janela de destino**: Dentro do painel lateral é possível cadastrar (`+ Novo`), editar (`Título`, `Atalho`, `Conteúdo`), excluir (`Excluir`), ordenar (`▲` / `▼`), configurar atalhos (com botão `Padrão`), alternar entre `Digitar` e `Colar`, escolher **Iniciar com o Windows** e marcar/desmarcar **Exibir texto** (`OcultarTexto` com prévia mascarada `••••••••`), preservando integralmente `%APPDATA%\HotKeyText\textos.xml`. Se uma edição avulsa do `HotKeyText` estiver em execução (`DetectStandaloneConflict`), o ConectaTI identifica o conflito, orienta o usuário a encerrá-la no banner do painel e permite ativar os atalhos integrados assim que ela for fechada. O rastreamento da última janela externa ativa (`LastExternalTargetWindow`) garante que a inserção ocorra na janela de destino correta mesmo após abrir e recolher o painel para gerenciar textos.
- **Ajustes em Tema, Configurações e barra central**:
  - **Tema**: O rótulo **Aparência** foi substituído por **Tema** no botão da barra superior (`ThemeButtonLabel`), no menu `Mais ▾` (`MenuToggleThemeItem`), na aba correspondente de Configurações e nas dicas/traduções (`Strings.resx`, `.en.resx`, `.es.resx`), mantendo o ícone sol/lua e preservando as preferências já salvas em `settings.json`.
  - **Configurações (engrenagem vetorial)**: O botão **Configurações** (`SettingsGearButton`) utiliza um ícone vetorial de engrenagem claramente reconhecível (`SettingsGearIconPath`, `FillRule="EvenOdd"` com 8 dentes definidos e círculo central vazado), legível no tamanho real do botão tanto no tema claro quanto no escuro, mantendo a dica **Configurações**.
  - **Barra de rolagem e divisor central**: O estilo global de `ScrollBar` (`App.xaml` + pincéis em `ThemeService`) desenha um indicador (`Thumb`) **fino, cinza e com pontas arredondadas** (`CornerRadius="2.5"`, `Width="4.5"` expandindo para `6` no hover), com trilho discreto transparente e maior contraste ao passar o mouse ou arrastar, preservando roda do mouse, teclado e arraste. O divisor central entre a lista e os detalhes (`MainPanelsSplitter`) mantém 6px de área confortável de pega e exibe apenas uma alça central vertical discreta (`SplitterGrip`).
- **Atualização e migração de 2 executáveis (1.8.0) para 1 executável (1.8.1)**: `AppUpdateService` e `tools/Publish-Release.ps1` validam e publicam o pacote de executável único (`ConectaTI-1.8.1-win-x64.zip` + `release-manifest.json`) consultando `MaxBSilva/ConectaTI`. Ao atualizar a partir da homologação 1.8.0 (que tinha `ConectaTI.exe` e `HotKeyText.exe`), o atualizador encerra processos em uso, faz backup de ambos os binários em `update-rollback/previous-install`, substitui `ConectaTI.exe`, remove o `HotKeyText.exe` legado da raiz da instalação e preserva conexões (`sqlite.db`), credenciais, `settings.json`, `backups/`, `textos.xml` e o salvamento explícito (`Salvar`/`Ctrl+S`), mantendo reversão completa em caso de falha.
- **Filtro de situação Todos / Ativos / Inativos**: Mantido integralmente no painel esquerdo (`ConnectionActivityFilter`: **Todos** por padrão, **Ativos** e **Inativos**), combinando com pesquisa por nome e visões (`Todos`, `Favoritos`, `Recentes`, `Lixeira`), ajustando seleção/foco/rolagem, movendo um item visível por vez nas setas `↑/↓` e preservando rascunhos em memória.

## Evolução de interface, aparência e atalhos — versão 1.8.0 (04/11/2026)

A versão 1.8.0 evolui a estética, a organização da interface, a responsividade e a navegação por teclado sobre a base estrutural da 1.7.0, preservando integralmente o esquema SQLite 10, os identificadores estáveis, os rascunhos em memória, o salvamento explícito e os formatos de transporte. Evidências comparativas (antes/depois), testes executados e análise de melhorias estão em [RELATORIO-EVOLUCAO-1.8.0.md](RELATORIO-EVOLUCAO-1.8.0.md).

- **Estética centralizada e indicadores não dependentes apenas de cor**: `App.xaml`, `ThemeService` e `ViewPreferencesService` concentram pincéis semânticos (`HeaderButton*`, `FocusBorderBrush`, `WarningSurfaceBrush`, `DangerSurfaceBrush`, `SubtleBadgeBackgroundBrush`), estilos de foco por teclado (`KeyboardFocusVisualStyle`), cartões e espaçamentos. O estado da senha exibe ícone textual além da cor (`✓`, `●`, `⚠`, `○`), pendências exibem faixa com ícone `●`, o filtro ativo da lista (`Todos`/`Favoritos`/`Recentes`/`Lixeira`) tem borda e destaque próprios, favoritos exibem `★` na lista e acessos inativos combinam barra lateral, texto vermelho e selo `[Inativo]`.
- **Diferenciação entre Aparência e Configurações**: Na barra superior, a alternância rápida de tema usa ícone vetorial sol/lua + rótulo **Aparência**, separada do botão **⚙ Configurações**. O diálogo de configurações foi organizado em 7 abas reais (**Geral**, **Aparência**, **Programas**, **Credenciais**, **Importação e backup**, **Atalhos** e **Sobre e diagnóstico**), cada uma com selo explícito de modo de aplicação (*Aplicado imediatamente*, *Aplicado ao clicar em Salvar* ou *Pré-visualizado imediatamente; confirmado ao Salvar*).
- **Responsividade e densidade independente do tamanho da fonte**: Os painéis esquerdo e direito permanecem sempre lado a lado, com `GridSplitter` limitado dinamicamente à largura útil da janela (`MainWindow.UpdateAdaptiveHeaderAndPaneBounds`). A barra superior substitui a rolagem horizontal por adaptação automática e menu `Mais ▾` (`TopOverflowButton`) em janelas estreitas. `ViewPreferencesService` calcula `FontSize` (11–18) e densidade (`Compacto`/`Confortável`) de forma independente (`ConnectionItemPadding`, `InputPadding`, `ButtonPadding`, `FormPadding`, `FieldSpacingMargin`, `CardPadding`).
- **Atalhos padronizados e correção do Escape**: `KeyboardShortcutsCatalog` centraliza os atalhos reais do aplicativo (`Ctrl+N`, `Ctrl+S`, `Ctrl+F`, `F2`, `Ctrl+,`, `F1`, `Enter`, `↑/↓`, `A–Z/0–9`, `Esc`, `Tab/Shift+Tab`), exibidos na aba **Atalhos** e nas dicas dos controles. Foi corrigido o tratamento de `Escape` em `MainWindow.Window_PreviewKeyDown`: pressionar `Esc` durante a edição de campos no painel direito fecha apenas menus/popups abertos e preserva tanto o filtro de busca quanto o rascunho em edição.

## Refatoração estrutural — versão 1.7.0 (04/11/2026)

A referência funcional 1.6.0 foi reexecutada antes das alterações: 257 testes aprovados. A versão 1.7.0 mantém apresentação e esquema 10; arquitetura atual em [ARQUITETURA.md](ARQUITETURA.md) e evidências em [RELATORIO-REFATORACAO-1.7.0.md](RELATORIO-REFATORACAO-1.7.0.md).

ConnectionEditWorkspace concentra sessões por ID e geração; ConnectionEditSession concentra intenções/revisões e reconciliação por ConnectionSaveSubmission. MainViewModel mantém bindings, proteção dos eventos e publicação no Dispatcher. EditorOperationGuard substitui as verificações duplicadas de validade do editor em cópia/revelação e no diálogo; também protege o diagnóstico de rede. PasswordRevealLease concentra sequência e prazo da apresentação da senha.

ConnectionListQuery contém busca/filtro/ordenação, seleção por ID e movimento limitado na ordem exibida; ConnectionListNavigation permanece o adaptador WPF de foco/virtualização. ConnectionCredentialService usa fronteiras estreitas de lookup/leitura/clipboard. SqliteSchemaMigrator contém DDL e backups prévios, separado do CRUD. Os codecs próprios CSV/XML/JSON e os contratos de importação foram separados da coordenação de prévia/commit. ConnectionPlanBuilder/CommandLineTokenizer constroem o plano, e IProcessRunner/IRdpPasswordEncoder isolam execução e codificação Windows.

IsActive continua classificação manual; IsDeleted continua lixeira; DNS/TCP não monitora nem altera a classificação. ActiveConnectionsCount/GetActiveCountAsync são nomes legados preservados, com aliases explícitos e textos “fora da lixeira”. Nenhum nome serializado do cadastro, configurações ou formatos foi alterado.

O Git tinha objetos ausentes no histórico de HEAD. Objetos necessários foram recuperados de um clone remoto separado, após backup dos metadados e fontes, sem reset/checkout de arquivos. A branch de trabalho é codex/refactor-1.7.0. Objetos antigos desconectados incompletos permanecem preservados; detalhes e limites no relatório.


## Correções prioritárias — versão 1.6.0 (03/11/2026)

MainViewModel captura um predicado de validade com referência da sessão, geração do editor, ID do acesso, ID da credencial e revisões do cadastro/senha antes dos awaits. Cópia individual/completa, recuperação, revelação, mensagens e cada tentativa do ClipboardService verificam esse predicado. Revelação tem sequência própria para descartar pedidos anteriores e a ocultação revalida cancelamento no Dispatcher. Fechamento invalida as sessões. Falhar/cancelar uma nova cópia preserva a limpeza temporizada da cópia anterior.

CredentialsDialog captura seleção/geração/revisão e invalida a revelação ao editar, navegar ou fechar. Salvar submete um snapshot independente; respostas antigas não limpam edições nem recarregam/selecionam outro registro. Métodos internos e operações virtuais estreitas do vault permitem regressões com atraso controlado, sem mudar o armazenamento real.

RdpFileService normaliza todos os inteiros com cultura invariável. Domínios: authentication level 1–3; enablecredsspsupport e negotiate security layer somente 1; enablerdsaadauth, prompts e promptcredentialonce 0–1; gatewayusagemethod 0–4, gatewaycredentialssource 0–5, gatewayprofileusagemethod 0–1. Tipos diferentes de i são rejeitados nessas propriedades, inclusive b antes da sanitização. A política anterior de bloquear nível zero é preservada. Build valida também opções estruturadas e o serviço de persistência chama Build antes do commit RDP. A cópia original conserva a grafia sanitizada para comparação, enquanto a saída efetiva é canônica.

CSV/XML próprios incluem SshClient/SshUseAgent/VncParameters; o mapeamento CSV da interface repassa os novos índices. XML lê SshClient e SshPreference. Ausência assume Default/true/vazio. Rótulos da cópia seguem o idioma da interface, sem traduzir valores do usuário nem propriedades técnicas RDP. Os três catálogos contêm as ações recentes, instruções, erros e restauração da fonte. Versão de startup/diagnóstico deriva do assembly. Esquema continua 10.

Build/testes/publicação reproduzíveis: tools/Publish-Release.ps1. A pasta da versão precisa ser nova; somente o executável e manifesto são distribuídos. Não alterar bases pessoais para testar. A suíte WPF usa uma Application isolada e Dispatcher STA ativo durante todo o processo, evitando a limitação WPF de uma única Application.


## Status Ativo/Inativo — 03/11/2026

ConnectionEntry.IsActive é observável, default true; integra Clone, comparação de valores editáveis e ActivityDisplay localizado. O painel usa o rascunho; a lista representa valores persistidos, atualizados após Salvar. Nenhuma seleção, busca, ordenação, launcher ou credencial é condicionada a IsActive. IsDeleted e Environment continuam independentes.

Migração 10 acrescenta Connections.IsActive INTEGER NOT NULL DEFAULT 1 CHECK(0,1), com transação e backup prévio pelo fluxo existente. JSON/pacotes usam o modelo; CSV/XML próprios incluem Ativo/IsActive e assumem ativo quando omitido. A etapa de mapeamento CSV da interface repassa ActiveColumnIndex e RdpConfigurationColumnIndex. A cópia completa inclui o status. Cores de seleção são dinâmicas por tema, com faixa e selo textual vermelhos. Ver `RELATORIO-ATIVO-INATIVO.md`.

## Identificação RDP — 30/10/2026

RdpSessionFile.CreatePath produz arquivo com nome/endereço/porta legíveis e pasta-pai aleatória por abertura. Não introduz propriedades RDP de título nem altera janelas por endereço/PID. Colon ASCII vira U+A789 somente no nome do arquivo; campos de conexão permanecem intactos. ArgumentList e transporte DPAPI/TERMSRV continuam. AppPaths limpa subpastas próprias antigas sem recursão e mantém arquivos enquanto MSTSC estiver ativo. Testes cobrem colisões paralelas, caracteres, Unicode, portas e preservação do conteúdo. MSTSC /edit real mostrou título genérico; título de sessão autenticada e barra de tarefas permanecem sem validação real por falta de servidor disponível. Ver `RELATORIO-IDENTIFICACAO-RDP.md`.

## Navegação da lista — 30/10/2026

Busca incremental e setas usam a ItemCollection exibida. ConnectionListNavigation sincroniza SelectedItem, container virtualizado e foco, com ScrollIntoView e UpdateLayout síncronos; só aplica foco se a lista já o possuir. Não há tarefa de busca/foco pendente. Setas simples são consumidas em PreviewKeyDown (Handled=true); modificadas preservam a seleção estendida WPF. O endpoint selecionado com foco serve de referência ao sair de uma seleção múltipla. Busca nativa IsTextSearchEnabled fica desligada apenas na lista; a busca personalizada com acentos normalizados permanece. Setas e mouse encerram o buffer e o timer só limpa status. Ver `RELATORIO-NAVEGACAO-LISTA.md` e tools/ConectaTI.UiSmoke/NavigationChecks.cs.

## Ajustes de cópia e interface — 29/10/2026

`MainViewModel.CopyAll` captura formulário/sessão antes de ler a credencial, respeita PasswordEditIntent e verifica revisão/geração antes de copiar. `ConnectionCopyFormatter` produz texto rotulado sem campos vazios nem opções desabilitadas. Nunca busca fallback no acesso anteriormente selecionado. Usa ClipboardService com hash e limpeza temporizada; a senha só é lida no comando explícito. Cópia não salva o formulário.

Fonte permanece na preferência existente FontSize (11–18; padrão 12,5), com restauração na aba Geral. ViewPreferencesService recalcula os recursos de fonte e alturas multilinha a cada alteração. Gateway/diagnóstico usam cores dinâmicas e checkboxes permitem quebra de texto. O cabeçalho mantém todas as ações em faixa horizontal rolável sem marca duplicada. RDP/Gateway, esquema SQLite e credenciais permanecem compatíveis. Ver `RELATORIO-INTERFACE-COPIA.md`.

Projeto desenvolvido e mantido por **Max Silva** ([MaxBSilva](https://github.com/MaxBSilva)).

Referência funcional e técnica revisada em 04/11/2026 para a versão 1.7.0, baseada na 1.6.0. Evidências novas e limites estão em [RELATORIO-REFATORACAO-1.7.0.md](RELATORIO-REFATORACAO-1.7.0.md). Relatórios antigos citados que não acompanham esta cópia são apenas referências históricas, não evidências desta execução.

## Para que serve

O ConectaTI é um aplicativo desktop Windows para profissionais de suporte que precisam organizar e abrir acessos remotos de vários clientes. Reúne nome do acesso, cliente, grupo, ambiente, endereço, usuário, credenciais e informações de VPN em uma lista pesquisável.

O aplicativo organiza os acessos e inicia clientes externos. Uma mensagem de cliente iniciado não comprova que o servidor aceitou a autenticação. O diagnóstico DNS/TCP também não comprova autenticação.

O funcionamento é local e portátil. A versão atual não possui servidor próprio, contas de equipe, sincronização centralizada ou edição simultânea de uma base compartilhada.

## Uso básico

1. Abra `ConectaTI.exe` em uma pasta com permissão de escrita.
2. Selecione um acesso na lista ou use **Nova conexão / Ctrl+N**.
3. Preencha nome, endereço, usuário e tipo de conexão. Campos adicionais ficam nas opções do formulário, incluindo informações opcionais de VPN.
4. Use **Salvar / Ctrl+S** para gravar o cadastro.
5. Use **Conectar**, Enter na lista ou duplo clique para iniciar o cliente configurado.

As visões **Todos**, **Favoritos**, **Recentes** e **Lixeira** mudam o conjunto exibido. A caixa de busca filtra a lista. Digitar com o foco na lista faz uma busca incremental por nome; digitar nos campos do formulário deve editar somente esses campos.

Os caminhos de MSTSC, PuTTY, OpenSSH, WinSCP e VNC são tratados pelas configurações e pela detecção de clientes externos. Configurar a conexão não instala esses programas.

### Tipos de conexão

| Tipo | Cliente externo | Uso |
| --- | --- | --- |
| RDP | MSTSC | Área de Trabalho Remota, inclusive opções de tela, monitores e sessão administrativa |
| SSH | PuTTY ou OpenSSH | Terminal remoto, com suporte a chave privada/agente conforme o cliente |
| SFTP / SCP | WinSCP | Transferência de arquivos |
| VNC | Visualizador configurado | Acesso gráfico com parâmetros específicos de cada visualizador |

O endereço aceita hostname, IPv4 ou IPv6 e porta opcional. `HostPortParser` separa endereço e porta; portas explícitas em IPv6 usam a forma `[endereço]:porta`. `UserIdentityFormatter` normaliza usuário simples, `DOMINIO\usuario` e UPN sem duplicar o domínio.

### Atalhos implementados na janela principal

| Atalho | Resultado |
| --- | --- |
| Ctrl+N | Abre um novo rascunho |
| Ctrl+S | Salva o formulário ativo quando há alterações válidas |
| Ctrl+F | Foca a busca |
| F2 | Foca o campo Nome da conexão no painel direito |
| Ctrl+, | Abre Configurações |
| F1 | Abre diretamente a aba Atalhos nas Configurações |
| Enter / duplo clique na lista | Conecta ao acesso selecionado (sem iniciar conexão ao pressionar Enter em campos de edição) |
| ↑ / ↓ na lista | Move exatamente um item visível por vez na ordem exibida |
| Esc na busca | Limpa a busca |
| Esc na lista | Limpa a busca incremental (ou o filtro de busca quando o buffer já estiver vazio) |
| Esc nos campos de edição | Fecha menus/popups abertos sem limpar a busca nem apagar o rascunho |

A janela principal não implementa Ctrl+L, bloqueio por senha mestra nem atalhos globais para copiar/revelar senhas. As senhas usam proteção automática do usuário Windows.

## Edição manual e rascunhos

Cada conexão persistida tem um ID estável. A seleção da lista representa os dados persistidos; o formulário trabalha com uma cópia de edição.

- Só **Salvar / Ctrl+S** persiste os campos do cadastro. Navegar e conectar não salvam o formulário.
- Ao editar A, abrir B e voltar a A, o rascunho de A é recuperado. Campos e senhas não devem passar de um acesso para outro.
- Novos cadastros recebem um GUID próprio antes da gravação e aparecem no seletor de novos rascunhos.
- Os rascunhos existem apenas em memória durante a sessão. Não há arquivo de rascunhos nem recuperação após fechar.
- **Descartar alterações** restaura os dados persistidos ou remove um novo rascunho. Durante uma gravação submetida, a ação fica indisponível para evitar uma corrida com a conclusão.
- Fechar não mostra confirmação nem salva rascunhos. Preferências da janela continuam sendo gravadas separadamente.

O botão Salvar é neutro e desabilitado sem mudanças. Com pendências, fica âmbar, mostra **Salvar alterações** e há um aviso textual. Durante a gravação, mostra **Salvando...** e impede submissões duplicadas. Em falha, preserva a edição para nova tentativa. Reverter os campos aos valores originais remove a pendência.

### Gravação com navegação ou edição simultânea

Antes do primeiro `await`, o ViewModel captura o ID, uma cópia dos dados e as revisões do formulário e da senha. A conclusão só limpa as alterações correspondentes à revisão gravada. Edições posteriores continuam pendentes, e salvar A não substitui o formulário de B.

Recargas e revelações de senha verificam a geração do editor para impedir que uma resposta antiga substitua uma seleção mais recente. A lista é preparada antes de publicar os resultados; uma falha de leitura deve conservar a visualização anterior.

## Senhas e credenciais

O formulário distingue três intenções: **manter**, **substituir** e **remover**.

| Situação | Indicador |
| --- | --- |
| Existe credencial persistida | Senha salva |
| Não existe credencial persistida | Sem senha salva |
| Nova senha ou substituição em edição | Senha alterada — salvar |
| Remoção solicitada | Remoção pendente — salvar |

Um campo vazio por proteção não significa pedido de exclusão. Editar nome ou endereço deve preservar a credencial existente. Apagar intencionalmente uma senha efetivamente revelada para edição, ou usar **Remover**, prepara a remoção; ela só é persistida ao salvar.

Mostrar ou ocultar senha altera sua apresentação e não cria pendência. Limpezas programáticas durante a troca de acesso são protegidas para não serem interpretadas como exclusão humana. Descartar restaura a intenção e o estado anteriores.

CredentialVaultService protege automaticamente novas senhas com DPAPI CurrentUser no SQLite. O envelope CTDPAPI2 identifica versão 2, escopo, hash público do SID do usuário, blob protegido e checksum de detecção de dano acidental. Entropia adicional vincula domínio do formato, versão, ID da credencial e origem; não é uma chave secreta. DPAPI autentica o conteúdo protegido. Checksum não substitui essa autenticação. Não há criação/desbloqueio/bloqueio diário nem chave fixa de criptografia.

No mesmo perfil, os comandos existentes conectam/revelam/copiam sem senha mestra. Origem identificada incompatível gera CredentialUserContextException e o aviso específico de outro usuário. Falhas de formato/integridade ou de DPAPI no mesmo contexto permanecem técnicas, sem inferir que toda CryptographicException significa outro usuário. DPAPI1 e envelopes sem versão seguem legíveis; se não contêm identificação de origem, uma falha não comprova troca de usuário. Um novo cadastro pode ser protegido no usuário atual sem modificar os copiados. O launcher inicia sem senha automática quando a leitura falha e informa que o cliente externo pedirá autenticação manual.

SecretStorage identifica Windows legado (0), SQLite/DPAPI (1) e cofre anterior AES-GCM (2). CTVAULT1 e MKGCM1 são mantidos para migração explícita. A senha mestra anterior serve apenas para converter segredos para CTDPAPI2, em uma transação com verificação de leitura, sem mudar IDs/compartilhamento. Cancelar, errar a senha ou falhar preserva o formato anterior. O diálogo de migração aparece quando há registros pendentes; não há controles de criar/alterar/bloquear cofre. Chaves antigas controladas pelo app são zeradas depois da conversão, inclusive em falha. Metadados e cópias protegidas anteriores permanecem para compatibilidade; nunca são consultados como fallback após edição ou remoção.

### Consistência e transporte

Conexões e credenciais novas entram na mesma transação SQLite. Substituições do formulário criam credencial dedicada, sem alterar outros acessos que compartilham a anterior. Cancelamento antes do commit e falha fazem rollback. Importações e salvamentos não chamam CredWriteW para segredos do aplicativo. Filas antigas e originais Windows são preservados. TERMSRV e arquivos .rdp protegidos pelo Windows pertencem ao lançamento MSTSC e são separados do armazenamento do aplicativo.

ExportSelectionModel mantém seleção por ID durante busca/filtros/revisão. ExportTransferPackageAsync captura IDs, valida o estado atual e prepara exclusivamente acessos ativos selecionados. Incluindo senhas, lê somente credenciais vinculadas e únicas; qualquer segredo indisponível impede a geração. Sem senhas, remove os vínculos e não recupera nenhum segredo. Não exporta lixeira, credenciais avulsas, banco ou chave global.

CTIENC02 usa AES-256-GCM, nonce aleatório de 12 bytes, tag de 16, PBKDF2-HMAC-SHA256 com 600.000 iterações e salt aleatório de 32 bytes. Cabeçalho com versão/algoritmos/parâmetros é AAD autenticado. O leitor limita parâmetros, tamanho e estrutura; CTIENC01 continua legível. CTIMETA2/.ctishare transporta somente metadados sem senhas e rejeita credenciais ou vínculos de segredos. Saída temporária contém apenas ciphertext ou metadados sem segredos, e é renomeada após preparação completa.

A importação autentica e valida o pacote antes de apresentar prévia e gravar. Mesclagem utiliza novos IDs de credenciais, preserva compartilhamento e protege os segredos com DPAPI do destino no mesmo commit dos acessos. A leitura de pacotes históricos com credenciais avulsas foi mantida por compatibilidade; novas exportações nunca incluem avulsas.

Backup local do SQLite conserva a proteção de origem. Restaurar não converte DPAPI para outro perfil; transferência com senhas exige pacote explícito e sua senha própria, nunca salva em configurações. Não gerar pacotes portáteis automaticamente.

Senhas não devem ser registradas em logs, relatórios, argumentos de processo ou arquivos temporários em texto puro. SSH, WinSCP e VNC preservam suas limitações de autenticação dos clientes externos.
## Armazenamento portátil

Por padrão, a raiz de dados é a pasta do executável:

| Caminho | Conteúdo |
| --- | --- |
| `sqlite.db` | Conexões, credenciais protegidas, migrações e fila de limpeza do cofre |
| `settings.json` | Preferências, caminhos dos clientes e geometria da janela |
| `backups/` | Backups da base |
| `logs/` | Diagnósticos locais |
| `temp/` | Arquivos temporários de sessão RDP |

Uma pasta personalizada previamente escolhida tem prioridade e é localizada pelo ponteiro `%LOCALAPPDATA%\ConectaTI\data_location.txt`. Se a pasta padrão não for gravável, o aplicativo oferece a seleção de outra pasta.

`DatabaseMigrationManager` trata descoberta e migração de bases legadas. `SqliteRepository.InitializeAndMigrate` aplica migrações de esquema e faz backup consistente antes de atualizar uma base existente. A versão 1.7.0 mantém o esquema 10 (RdpOptionsJson na migração 9 e IsActive na 10). O esquema 8 introduziu: origens explícitas, formatos legados, AppVaultMetadata/AppVaultNonces históricos e CredentialProtectionHistory para conservar fontes criptografadas durante a migração. Há backup consistente antes de migrar bases antigas.

Não editar a base do usuário em testes nem sobrescrevê-la ao publicar. SQLite não deve ser usado como banco de equipe com múltiplas instâncias gravando em um compartilhamento de rede.

## Importação, exportação e lixeira

- `ImportExportService` reúne importações/exportações e tratamento de duplicidades.
- `AutoPuttyCodec` trata formatos AutoPuTTY suportados e o formato XML de intercâmbio. Arquivos proprietários criptografados exigem uma exportação compatível na origem.
- CSV e JSON podem transportar cadastros; a exportação padrão não inclui senhas em texto puro.
- O pacote `.ctienc` permite transporte criptografado de conexões e credenciais com senha de transporte.
- Backups e restauração usam mecanismos consistentes do SQLite e verificações de integridade.
- Excluir um acesso normalmente o move à lixeira; exclusão definitiva e esvaziamento são ações distintas com confirmação própria. Essas confirmações não são diálogos de salvamento.

## Idiomas e preferências

`LocalizationService` resolve Automático pelo idioma de interface do Windows e mapeia variantes de pt/en/es. Outras línguas usam pt-BR. O catálogo central `Localization/Strings.resx`, com satélites `.en.resx` e `.es.resx`, não utiliza serviços externos. A extensão WPF `Loc` atualiza bindings; observadores fracos atualizam textos calculados e controles já existentes, inclusive abas não ativas. A troca não executa recarga da base nem gravação do cadastro.

`ToInvariantDisplayName` mantém a representação dos filtros e do CSV separada de `ToDisplayName`. Nomes de clientes, grupos, acessos e outros campos do usuário não passam pelo tradutor. A cultura usada para armazenamento técnico continua independente do idioma de exibição.

`ViewPreferencesService` valida fonte (11–18), divisórias e colunas antes de aplicar recursos dinâmicos. Densidade e fonte não substituem a coleção de acessos. As colunas da prévia são identificadas pelo caminho do campo, sem depender do título traduzido ou da ordem visual.

## Importação e edição em lote

A leitura assíncrona utiliza um escopo cancelável, limites de tamanho/estrutura, XML sem entidades externas e uma prévia virtualizada. A derivação Argon2 conserva os parâmetros de compatibilidade: 64 MiB, 4 iterações e paralelismo 16 dentro de uma derivação. Registros são processados sequencialmente. Há cache limitado a 64 chaves por leitura, apagado ao final; isso ajuda somente quando sal e senha são repetidos.

O commit captura a seleção antes dos awaits e consulta duplicidades novamente no banco. Registros ignorados não criam credenciais. Linhas do mesmo arquivo que atualizam um mesmo ID são consolidadas e refletidas no resumo. Credenciais novas recebem IDs dedicados. Conexões e credenciais entram em uma transação; o armazenamento DPAPI compartilha o mesmo semáforo das gravações manuais e não prepara cópias no Credential Manager. Um cancelamento antes do commit causa rollback. Depois do commit, o resultado permanece concluído.

O pacote `.ctienc` também usa a prévia na interface e o mesmo commit. A lista principal é recarregada de forma consolidada ao fechar as configurações após alterações. A seleção, filtros e sessões de edição são preservados quando ainda aplicáveis.

A seleção múltipla abre `BatchEditDialog`. `ApplyBatchEditAsync` atualiza somente colunas marcadas em uma transação. Limpar cliente/grupo persiste vazio; não alterar conserva o valor. Baselines das sessões são atualizados nos campos submetidos, preservando rascunhos dos outros campos e edições posteriores. Senhas não fazem parte do lote. O menu de contexto copia Host ou RawEffectiveUser; não existe campo IP separado no modelo.

A falha de recuperação da credencial é distinta de ausência de senha. Diagnósticos registram etapa, hora UTC, exceção e contexto da importação; Working Set é explicitamente uma leitura do momento da coleta. Logs são sanitizados, limitados a 512 KiB e têm retenção de até 12 arquivos.

`--data-dir` permite iniciar com uma pasta explícita e pula a descoberta de bases legadas. O pacote inclui um iniciador de teste que usa essa opção. Não substitui nem altera o ponteiro da pasta de dados habitual.

## Arquitetura e mapa do código

A solução é C#/.NET 10 para Windows, WPF com MVVM e SQLite. As dependências de produção declaradas são `CommunityToolkit.Mvvm`, `Microsoft.Data.Sqlite` e `Konscious.Security.Cryptography.Argon2`.

| Local | Responsabilidade |
| --- | --- |
| `src/ConectaTI.App/App.xaml.cs` | Composição dos serviços, migração inicial e abertura da janela |
| `MainWindow.xaml` e `.xaml.cs` | Interface, atalhos, foco, eventos e sincronização protegida dos controles de senha |
| `ViewModels/MainViewModel.cs` | Fachada WPF dos comandos/bindings, publicação das listas e coordenação do editor |
| `Models/ConnectionEntry.cs` | Cadastro observável, identidade e comparação dos campos editáveis |
| `Models/ConnectionEditSession.cs` | Cópia de edição, baseline, revisões, intenção de senha e reconciliação de submissões |
| `Services/ConnectionPersistenceService.cs` | Coordenação do commit SQLite e operações no cofre |
| `Data/SqliteRepository.cs` | Acesso ao SQLite, transações e backups; evolução delegada a SqliteSchemaMigrator |
| `Data/DatabaseMigrationManager.cs` | Localização e migração de bases legadas |
| `Security/` | Proteção de segredos, integração nativa Windows e área de transferência |
| `Services/Launchers/` | Detecção dos clientes, construção dos argumentos e início de processos |
| `Services/ImportExport/` | Importação, exportação, AutoPuTTY e pacotes de transporte |
| `Services/Diagnostics/` | Diagnóstico de aplicação, DNS e TCP |
| `Views/` | Diálogos auxiliares de configuração, importação e credenciais |
| `tests/ConectaTI.Tests/` | Testes xUnit de persistência, segurança, importação, edição e WPF |
| `tools/ConectaTI.Repro/` | Reprodução isolada do problema histórico de lista/thread |
| `tools/ConectaTI.UiSmoke/` | Janela real com dados fictícios em pasta própria, sem descoberta de bases pessoais |
| `samples/` | Exemplos fictícios para importação |

As atualizações de propriedades e coleções vinculadas devem ocorrer no Dispatcher WPF. Operações demoradas de banco e criptografia ficam fora da thread da interface. Não envolver toda a rotina de salvamento em `Task.Run`, pois parte dela atualiza a UI.

Proteções de eventos devem restaurar o valor anterior com `try/finally`, inclusive em chamadas aninhadas. Não usar `.Wait()`, `.Result` ou `GetAwaiter().GetResult()` no fluxo de navegação/salvamento.

## Desenvolvimento e publicação

Use Windows com SDK .NET 10. O executável publicado como self-contained não exige o SDK. Nesta entrega, o SDK 10.0.403 foi usado em `%LOCALAPPDATA%\ConectaTI-Development\dotnet\dotnet.exe`.

```powershell
dotnet build ConectaTI.slnx -c Release
dotnet test ConectaTI.slnx -c Release
dotnet run --project src/ConectaTI.App/ConectaTI.App.csproj
```

Executar o app normal pode descobrir bases legadas do computador. Para testes visuais isolados, prefira:

```powershell
dotnet run --project tools/ConectaTI.UiSmoke -c Release
```

Publicação portátil em pasta separada:

```powershell
dotnet publish src/ConectaTI.App/ConectaTI.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o dist/ConectaTI-1.8.0-win-x64
```

Distribuir somente os arquivos de publicação previstos. Não incluir SQLite, configurações, logs, backups ou credenciais. Para atualizar uma instalação existente, preservar os dados e substituir o executável após backup.

O `.gitignore` mantém `bin/`, `obj/`, `artifacts/`, `dist/`, arquivos locais de usuário e pacotes fora do histórico Git. Executáveis de distribuição são artefatos separados do código-fonte.

## Validação e limites conhecidos

Os relatórios de validação 1.5.0 e 1.2.1 foram citados no material histórico, mas não acompanham esta cópia. Não representam evidência nova. Os resultados desta entrega estão em RELATORIO-REFATORACAO-1.7.0.md. Os logs históricos relatavam CredWriteW/Win32 8; a causa interna do erro e o XML original permanecem sem homologação no ambiente do incidente.

As regressões essenciais são seleção ↔ formulário, lista após salvar, filtros/abas, rascunhos A/B, novas identidades, edições durante save, reverter/descartar, senha protegida/revelada/removida, compartilhamento de credenciais, rollback e falhas do cofre.

Continuam dependendo do ambiente do usuário: autenticação real nos servidores, clientes externos instalados, permissões locais, DPI/múltiplos monitores e a sequência exata do vídeo que não foi anexado. A presença de testes de launcher não comprova uma sessão remota autenticada.

## Direção para próximas alterações

Manter o aplicativo leve e portátil. Priorizar correções pontuais e preservar AutoPuTTY e os dados existentes. Mudanças em seleção, edição ou credenciais devem manter os contratos descritos neste documento e acrescentar regressões quando houver um defeito novo.

Servidor central, permissões de equipe e sincronização são evoluções futuras, não funcionalidades implementadas nesta versão. Caso sejam solicitadas, precisam de projeto e migração próprios, sem transformar o SQLite portátil em um banco compartilhado improvisado.

### Diagnóstico da confirmação de importação

CredWriteW captura seu erro nativo imediatamente, antes da limpeza dos buffers. A confirmação registra a primeira falha com etapa, código nativo quando aplicável, índice do registro, linha de origem e posição da credencial (base 1), tipo e stack, sem senhas ou dados dos acessos. A proteção DPAPI e as operações SQLite têm etapas separadas. Capturas externas preservam esse diagnóstico; erro nativo 8 não deve ser confundido automaticamente com System.OutOfMemoryException. O print de 225 conexões/222 válidas/225 senhas recuperadas direciona a investigação à confirmação, mas ainda falta reproduzir o incidente original. O relatório histórico dessa investigação não acompanha esta cópia.
# Extensão RDP / RD Gateway — 29/10/2026

`ConnectionEntry.RdpOptions` agrupa campos observáveis de gateway, propriedades adicionais e cópia sanitizada do original. Eventos filhos notificam a sessão; Clone é profundo e HasSameEditableValues inclui o grupo. A importação cria sessão nova com baseline vazio, habilitando Salvar sem persistência automática.

`RdpFileService` centraliza leitura com BOM, parser com divisão nos dois primeiros dois-pontos, normalização restrita de /admin, sanitização e geração. Campos estruturados substituem chaves equivalentes; outras propriedades permanecem sem duplicatas. Senhas, propriedades com password/secret/token, binários e assinatura são excluídos da importação/diagnóstico. São rejeitadas configurações que desabilitam CredSSP, negociação de segurança ou aceitam certificado sem aviso.

Migração 9 acrescenta RdpOptionsJson (default {}), com transação e backup prévio existente. JSON e pacotes incluem o grupo; CSV/XML próprios recebem metadados RDP, incluindo tela/monitores/admin. Backups SQLite abrangem a coluna.

O launcher mantém ArgumentList, UseShellExecute=false, DPAPI password 51 e TERMSRV. Cada abertura usa arquivo temporário com nome/endereço legíveis em pasta exclusiva aleatória e /admin separado. BuildSafeRdpPreviewAsync usa a mesma geração e estado das credenciais, sem produzir blob de senha. A limpeza ocorre após um dia e sem processo MSTSC. Cliente iniciado não significa autenticação concluída. Consulte `RELATORIO-RDP-GATEWAY.md`.

# Sites na inicialização, tecla de Textos rápidos, cópia de credenciais e atalhos personalizáveis

- **Inicialização com o Windows e até 3 sites (`StartupWebsitesService`)**: `AppSettings` armazena `OpenStartupWebsites` e `StartupWebsite1..3` em `settings.json` (sem alterar o esquema SQLite v10). `StartupWebsitesService` aceita apenas URLs `http://` ou `https://` (prefixando `https://` quando omitido, rejeitando caminhos locais/UNC/esquemas não web e deduplicando URLs). `HotKeyTextService.StartWithWindows` grava `"<ConectaTI.exe>" --windows-startup` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` e `UpgradeStartupCommandIfPresent` atualiza registros anteriores sem o argumento. `App.OnStartup` só abre os sites automaticamente quando iniciado com `--windows-startup`; em **Configurações → Geral**, o botão **Abrir sites agora** permite disparo imediato sob demanda.
- **Tecla de abertura dos Textos rápidos (`HotKeyTextConfig.AtalhoAtivacao`)**: Mantém `Scroll Lock` (`0x91`) como padrão quando vazio em `%APPDATA%\HotKeyText\textos.xml`. Permite teclas dedicadas sem modificador (`Scroll Lock`, `Pause`, `Insert`, `Num Lock`, `F1–F24`) ou combinações com `Ctrl`/`Alt` para teclas comuns, evitando bloquear a digitação global. Aplica-se tanto ao hook `WH_KEYBOARD_LL` quanto ao polling WTS/RDP (`OnWtsPollingTick`) e atualiza o cabeçalho do popup flutuante e do painel lateral.
- **Cópia em `CredentialsDialog`**: Preserva o construtor compatível `(SqliteRepository, CredentialVaultService, ITextClipboard?, SettingsService?)`. Adiciona cópia de usuário, senha e dados completos da credencial (`Credencial`, `Usuário`, `Senha`, `Observações`) no formulário e no menu de contexto da lista, usando `EditorOperationGuard` antes de `await` e `ClipboardService.CopyTextWithRetryAsync` com limpeza automática por hash SHA-256 quando há segredo.
- **Atalhos personalizáveis (`KeyboardShortcutsCatalog` + `AppSettings.CustomShortcuts`)**: Mantém os 11 itens do catálogo e os padrões `Ctrl+N`, `Ctrl+S`, `Ctrl+F`, `F2`, `Ctrl+,` e `F1`. Permite customizar as 6 ações da janela principal em **Configurações → Atalhos**, bloqueando conflitos entre ações e reservando `Ctrl+C`, `Ctrl+V`, `Ctrl+X`, `Ctrl+A`, `Ctrl+Z` e `Ctrl+Y` para edição de texto.
- **Abertura inteligente de VPN (`VpnClientLauncherService` + `VpnPromptDialog`)**: Sem migração de banco (reutiliza `VpnName`, `VpnApp`, `VpnServer`, `VpnNotes` do esquema v10). `VpnClientLauncherService` normaliza acentos/espaços/grafias (`fortcliente`, `open connect`, `open gui`, etc.), inspeciona caminhos explícitos, processos em execução, diretórios `Program Files`/`LocalAppData`, `App Paths` do Registro e atalhos `.lnk` do Menu Iniciar (com fallback para `rasphone.exe` / `ms-settings:network-vpn`). Em `ConnectAsync`, quando `CheckVpnBeforeConnect` está ativo e o acesso possui VPN configurada, faz sonda TCP rápida de 1200 ms (respeitando RD Gateway em modo sempre ativo na porta 443) e, se inacessível, apresenta `VpnPromptDialog` (`Abrir VPN`, `Conectar mesmo assim`, `Cancelar`), sem persistir edições pendentes.
- **Teste de WSDL por cliente**: Analisado e mantido fora desta entrega por decisão de produto (evitar complexidade de editor XML/SOAP e nova migração de banco no momento).
- **Pipeline em nuvem (`.github/workflows/release.yml`) e verificação atual**: Em ambiente Linux de nuvem (SDK 10.0.115), foi validada a compilação cruzada `dotnet build ConectaTI.slnx -c Release -p:EnableWindowsTargeting=true` e `dotnet build tools/ConectaTI.UiSmoke/ConectaTI.UiSmoke.csproj -c Release -p:EnableWindowsTargeting=true` com **0 erros e 0 avisos**, além da paridade dos 863 recursos em `Strings.resx`, `Strings.en.resx` e `Strings.es.resx`. A execução real da suíte xUnit/WPF STA (`dotnet test`) requer runtime Windows (`Microsoft.WindowsDesktop.App`), coberto localmente no Windows ou automaticamente pelo workflow `.github/workflows/release.yml` em `windows-latest`.


