# Relatório de Evolução — ConectaTI 1.8.0

Data: **04/11/2026**  
Base de partida: **ConectaTI 1.7.0** (esquema SQLite 10)  
Versão entregue: **ConectaTI 1.8.0** (`1.8.0.0`, esquema SQLite 10 preservado)

---

## 1. Diagnóstico Inicial (Referência 1.7.0)

Antes de qualquer alteração no código, foram geradas capturas de referência da versão 1.7.0 em `artifacts/ui-comparison/before-1.7.0/` e inspecionados os arquivos `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`, `ThemeService.cs`, `ViewPreferencesService.cs`, `LocalizedWindow.cs` e os diálogos de configuração.

### Problemas concretos identificados na 1.7.0

1. **Barra superior com rolagem horizontal em janelas estreitas e ícones ambíguos**:
   - Em `MainWindow.xaml`, o cabeçalho usava um `ScrollViewer` horizontal que exibia uma barra de rolagem sobre os comandos principais em janelas estreitas (`480px`), ocultando os botões da direita (`Tema` e `⚙`).
   - O botão de configurações mostrava apenas `⚙` sem rótulo textual, enquanto a alternância de tema mostrava texto solto (`☀ Claro` / `🌙 Escuro`) sem diferenciar claramente a preferência rápida de **Aparência** do diálogo completo de **Configurações**.
2. **Mistura entre Geral, Aparência e Diagnóstico nas Configurações**:
   - Em `ExecutablesSettingsDialog.xaml`, a aba **Geral** concentrava idioma, tema, densidade, tamanho da fonte e limpeza da área de transferência sem separação temática nem indicação clara do modo de aplicação de cada ajuste (imediato vs. ao salvar).
   - Não existia aba de consulta rápida de **Atalhos**.
3. **Defeitos de layout e localização em `ExecutablesSettingsDialog` e `LocalizedWindow`**:
   - Em `ExecutablesSettingsDialog.xaml`, os `DockPanel` dos caminhos de clientes externos posicionavam o botão **Procurar...** antes do texto de status (`Encontrado` / `Não encontrado`) sem `DockPanel.Dock="Right"`, comprimindo o `TextBox` do caminho para a direita.
   - Em `LocalizedWindow.RefreshElement`, ao atualizar o idioma da janela, um `TextBlock` com múltiplos `Inline` (como `FontSizeValueText`, que continha `<Run Text="{loc:Loc SizeLabel}"/>: <Run x:Name="FontSizeValueRun"/>`) tinha sua coleção `Inlines` substituída por `tb.Text`, apagando o valor numérico do tamanho da fonte e deixando-o com cor escura sobre fundo escuro.
   - Os contêineres raiz dos diálogos secundários usavam `Background="Transparent"`, resultando em fundo preto nas capturas offscreen (`RenderTargetBitmap`) e menor contraste de superfície.
4. **Dependência excessiva de cor e falta de estilo explícito de foco por teclado**:
   - Botões de filtro de visão (`Todos`, `Favoritos`, `Recentes`, `Lixeira`) não destacavam visualmente qual filtro estava ativo.
   - O estado da senha dependia apenas de cor (verde, âmbar, vermelho), sem prefixo simbólico (`✓`, `●`, `⚠`, `○`).
   - Itens favoritos na lista não exibiam indicador `★` ao lado do nome.
   - Controles interativos não compartilhavam um `FocusVisualStyle` de alto contraste padronizado pelo tema.
5. **Comportamento inesperado da tecla `Escape` durante edição de campos com busca ativa**:
   - Em `MainWindow.xaml.cs` (`Window_PreviewKeyDown`), pressionar `Esc` enquanto o usuário editava qualquer campo do formulário direito com um filtro `SearchText` ativo limpava silenciosamente `ViewModel.SearchText`, alterava a lista filtrada e roubava o foco para `SearchTextBox`.

---

## 2. Melhorias Implementadas na Versão 1.8.0

### 2.1. Estética, Hierarquia Visual e Acessibilidade
- **Centralização de recursos em `ThemeService.cs`, `ViewPreferencesService.cs` e `App.xaml`**:
  - Adicionados pincéis semânticos nos temas Claro e Escuro: `HeaderButtonBackgroundBrush`, `HeaderButtonHoverBrush`, `HeaderButtonBorderBrush`, `FocusBorderBrush`, `WarningSurfaceBrush`, `WarningBorderBrush`, `DangerSurfaceBrush`, `DangerBorderBrush` e `SubtleBadgeBackgroundBrush`.
  - Criado `KeyboardFocusVisualStyle` centralizado para todos os botões, caixas de texto, combos, checkboxes e itens de lista.
  - Padronizados `HeaderSecondaryButtonStyle`, `SectionCardBorderStyle` e `FieldLabelTextStyle`.
- **Estados comunicados além da cor**:
  - `MainViewModel.PasswordStatusIcon` adiciona ícones explícitos (`✓`, `●`, `⚠`, `○`) ao selo de estado da senha.
  - O aviso de pendências exibe faixa destacada com ícone `● Alterações não salvas`.
  - Os botões de filtro (`Todos`, `Favoritos`, `Recentes`, `Lixeira`) destacam a visão ativa com borda de destaque, fundo diferenciado e tipografia semibold.
  - Acessos favoritos exibem `★` na lista; acessos inativos mantêm a identificação tripla (barra lateral vermelha, nome em vermelho e selo `[Inativo]`).
- **Acessibilidade e dicas com atalhos**:
  - Adicionados `AutomationProperties.Name` e `ToolTip` com função + atalho em todos os comandos principais (`+ Nova (Ctrl+N)`, `Buscar (Ctrl+F)`, `Salvar (Ctrl+S)`, `Aparência`, `Configurações (Ctrl+,)`).

### 2.2. Diferenciação entre Configurações e Aparência
- **Barra superior**:
  - Botão **Aparência** com ícone vetorial próprio (Sol em modo escuro / Lua em modo claro) + rótulo textual `Aparência`.
  - Botão **⚙ Configurações** com ícone de engrenagem + rótulo textual `Configurações` (`Ctrl+,`).
- **Diálogo de Configurações reorganizado em 7 abas reais (`ExecutablesSettingsDialog`)**:
  1. **Geral**: Idioma da interface e limpeza automática da área de transferência.
  2. **Aparência**: Tema (Escuro/Claro), Densidade da interface (Compacto/Confortável) e Tamanho da fonte (11–18) com botão de restauração (12,5).
  3. **Programas**: Caminhos e detecção de MSTSC, PuTTY, OpenSSH, WinSCP e VNC (com layout corrigido para exibir o status à direita).
  4. **Credenciais**: Gerenciador de credenciais salvas e migração do formato legado quando aplicável.
  5. **Importação e backup**: Central de importação/exportação, backup local SQLite, restauração e pasta de dados.
  6. **Atalhos**: Consulta rápida e localizada de todos os atalhos de teclado (`KeyboardShortcutsCatalog`).
  7. **Sobre e diagnóstico**: Versão, caminho dos dados, resumo de diagnóstico e cópia do relatório sanitizado.
- Cada seção exibe um selo explícito de aplicação (*Aplicado imediatamente*, *Aplicado ao clicar em Salvar* ou *Pré-visualizado imediatamente; confirmado ao Salvar*).

### 2.3. Responsividade e Independência entre Fonte e Densidade
- **Painéis sempre lado a lado**:
  - Mantidas as duas colunas com `GridSplitter` (`MainSplitter`). `MainWindow.UpdateAdaptiveHeaderAndPaneBounds` limita dinamicamente a largura máxima do painel esquerdo conforme a largura atual da janela, garantindo que o painel de detalhes à direita nunca fique espremido ou oculto.
- **Cabeçalho adaptativo sem barra de rolagem horizontal**:
  - Em janelas estreitas (`< 720px` de largura efetiva ajustada pela escala da fonte), os botões `Importar RDP` e `Editar em lote` são recolhidos automaticamente para o menu **Mais ▾** (`TopOverflowButton`), mantendo `+ Nova`, `Mais ▾`, `Aparência` e `⚙ Configurações` sempre acessíveis.
- **Fonte (11–18) e Densidade (Compacto / Confortável) independentes**:
  - `ViewPreferencesService.Apply` calcula separadamente os recursos tipográficos (`AppFontSize`, `SectionFontSize`, `SmallFontSize`, `BadgeFontSize`) e os espaçamentos estruturais (`ConnectionItemPadding`, `InputPadding`, `ButtonPadding`, `FormPadding`, `FieldSpacingMargin`, `CardPadding`), permitindo combinar letras grandes (`15`–`18`) com densidade compacta.

### 2.4. Atalhos de Teclado e Navegação
- **Catálogo centralizado (`Services/KeyboardShortcutsCatalog.cs`)**:
  - Inventário único e localizado de 11 atalhos: `Ctrl+N`, `Ctrl+S`, `Ctrl+F`, `F2`, `Ctrl+,`, `F1`, `Enter`, `↑ / ↓`, `A–Z / 0–9`, `Esc` e `Tab / Shift+Tab`.
- **Correção do `Escape` contextual**:
  - Se o menu `Mais ▾` estiver aberto, `Esc` fecha apenas o menu.
  - Se o foco estiver em `SearchTextBox` ou em `ConnectionsListBox`, `Esc` limpa a busca incremental e/ou o filtro de busca.
  - Se o foco estiver nos campos de edição do painel direito, `Esc` **não** limpa `SearchText`, **não** move o foco para a busca e **não** descarta o rascunho.

---

## 3. Validação Executada Nesta Entrega

Todos os testes abaixo foram executados diretamente nesta entrega (não são resultados históricos):

1. **Compilação Release**:
   - Comando: `dotnet build ConectaTI.slnx -c Release`
   - Resultado: **0 avisos, 0 erros**.
2. **Suíte automatizada completa (xUnit + STA WPF)**:
   - Comando: `dotnet test ConectaTI.slnx -c Release --logger "trx;LogFileName=release-1.8.0.trx" --results-directory artifacts/test-results`
   - Resultado: **290 aprovados, 0 falhas, 0 ignorados** (inclui os 287 testes da 1.7.0 + 3 novos testes abrangentes em `Evolution180Tests.cs`).
3. **Validação prática de interface e fumaça (`ConectaTI.UiSmoke`)**:
   - `dotnet run --project tools/ConectaTI.UiSmoke -c Release -- --refactor-checks --narrow --font=18 --dark` → **Aprovado (código 0)**, validando edição sem auto-save, rascunhos por ID, troca A↔B, cópia individual/completa, cancelamento de diagnóstico atrasado e estado Ativo/Inativo.
   - `dotnet run --project tools/ConectaTI.UiSmoke -c Release -- --navigation --navigation-checks --narrow --font=18 --dark` → **Aprovado (código 0)**, validando busca por digitação, sincronização de seleção/foco/rolagem e movimentação exata de 1 item visível por vez com `↑/↓`.
4. **Capturas comparáveis de Antes (1.7.0) e Depois (1.8.0)**:
   - Antes (`1.7.0`): `artifacts/ui-comparison/before-1.7.0/`
   - Depois (`1.8.0`): `artifacts/ui-comparison/after-1.8.0/` (incluindo janela padrão `1366x768` escura, janela mínima `480x620` com fonte 18, janela `1000x680` em tema claro + compacto + fonte 15, e todas as 7 abas de Configurações).
5. **Publicação limpa**:
   - Pasta: `dist/ConectaTI-1.8.0-win-x64` (preservada `dist/ConectaTI-1.7.0-win-x64`).
   - Executável: `ConectaTI.exe` (`67.042.101` bytes, versão `1.8.0.0`).
   - SHA-256 (`release-manifest.json`): `2323B3AF46DE7794592B9D702F1EBB8D77C977E2AD9443508BC0686F3D3ADE60`.

### Cenários dependentes de ambiente externo (não validados em servidor real)
- Autenticação real de sessão RDP em servidor Windows remoto (o título pós-autenticação e a barra de tarefas do MSTSC dependem de um host RDP acessível).
- Validação visual em monitores físicos múltiplos com escalas de DPI distintas do Windows (testado via layout parametrizado e fontes 11–18 em STA).

---

## 4. Análise Priorizada de Próximas Melhorias no Aplicativo Inteiro (Etapa 9)

Abaixo está a análise técnica completa do aplicativo após a entrega da versão 1.8.0, separando **defeitos/limitações confirmados** de **propostas de evolução**, sem ampliar automaticamente o escopo desta entrega.

### Parte A — Defeitos ou Limitações Técnicas Confirmadas

#### A1. Derivação Argon2id sequencial na importação de arquivos AutoPuTTY grandes
- **Problema encontrado e evidência**: Em [`AutoPuttyCodec.cs`](src/ConectaTI.App/Services/ImportExport/AutoPuttyCodec.cs#L583-L618), cada senha criptografada com Argon2id (`64 MiB`, `4` iterações, grau de paralelismo `16`) é derivada sequencialmente no loop de leitura, e o `CancellationToken` só pode ser verificado antes/depois de cada derivação individual. O cache em memória (`MaxCachedDerivedKeys = 64`) só evita trabalho quando múltiplos registros compartilham exatamente o mesmo `salt` e senha.
- **Impacto no uso**: Em arquivos AutoPuTTY legados com centenas de registros usando salts distintos, a etapa de prévia pode levar dezenas de segundos e o botão Cancelar aguarda o término do bloco Argon2 corrente (~200–400 ms).
- **Melhoria proposta**: Reportar progresso granular por item decodificado (com estimativa de tempo restante na barra de progresso da prévia) e permitir que o usuário pré-visualize imediatamente os metadados dos cadastros enquanto a decodificação das credenciais ocorre sob demanda ou em lote controlado com feedback por item.
- **Esforço e risco aproximados**: **Médio** | Risco **Baixo** (desde que o commit continue exigindo a conclusão da leitura dos segredos selecionados).
- **Critério de conclusão**: Prévia de arquivo com 200+ conexões Argon2 exibe progresso contínuo por registro e responde ao cancelamento em menos de 300 ms, mantendo 100% dos testes de `AutoPuttyCodec` aprovados.

#### A2. Carregamento integral de `Connections` em memória a cada filtro ou inicialização
- **Problema encontrado e evidência**: Em [`MainViewModel.cs`](src/ConectaTI.App/ViewModels/MainViewModel.cs#L951-L995) e [`SqliteRepository.cs`](src/ConectaTI.App/Data/SqliteRepository.cs#L240-L310), `GetAllConnectionsAsync(includeDeleted: true)` carrega todos os registros e deserializa `RdpOptionsJson` para cada linha antes de aplicar `ConnectionListQuery.BuildView` em memória.
- **Impacto no uso**: Para bases típicas (até ~2.000 acessos) o desempenho é instantâneo, mas próximo ao limite suportado pela importação (`20.000` acessos) há maior alocação de objetos `ConnectionEntry` e `RdpConnectionOptions` na inicialização e após operações em lote.
- **Melhoria proposta**: Adiar a desserialização de `RdpOptionsJson` (lazy parsing no `ConnectionEntry` apenas quando o registro é inspecionado, copiado, exportado ou conectado) e manter projeção leve para a listagem.
- **Esforço e risco aproximados**: **Baixo a Médio** | Risco **Baixo** (transparente para `Clone()` e `HasSameEditableValues()`).
- **Critério de conclusão**: Redução de pelo menos 50% no tempo de carga e nas alocações de memória ao abrir uma base sintética com 10.000 conexões, sem alterar nenhum contrato de edição, cópia ou exportação.

---

### Parte B — Sugestões Priorizadas de Evolução (Usabilidade, Arquitetura, Acessibilidade e Manutenção)

#### B1. [Prioridade 1 — Usabilidade] Filtros rápidos combinados por Cliente, Grupo e Protocolo na barra da lista
- **Problema encontrado e evidência**: Na janela principal ([`MainWindow.xaml`](src/ConectaTI.App/MainWindow.xaml#L140-L195)), a lista possui busca textual (`SearchText`) e 4 visões (`Todos`, `Favoritos`, `Recentes`, `Lixeira`), enquanto os filtros estruturados por `Cliente`, `Grupo` e `Ambiente` existem apenas nos diálogos de Importação/Exportação ([`ImportExportDialog.xaml`](src/ConectaTI.App/Views/ImportExportDialog.xaml)).
- **Impacto no uso**: Analistas de suporte que atendem dezenas de clientes precisam digitar o nome do cliente na caixa de busca, não podendo combinar facilmente "filtrar pelo Cliente X" + "buscar servidor SQL" na lista principal.
- **Melhoria proposta**: Adicionar seletores compactos (ou faixa recolhível de filtros) por **Cliente** e **Protocolo/Ambiente** logo abaixo da caixa de busca na lista principal, integrados ao `ConnectionListQuery`.
- **Esforço e risco aproximados**: **Baixo** | Risco **Muito Baixo** (`ConnectionListQuery` já é isolado e testável).
- **Critério de conclusão**: Seleção de Cliente/Protocolo filtra a lista em conjunto com `SearchText` e as visões `Todos/Favoritos/Recentes/Lixeira`, preservando a seleção por ID e a navegação por setas.

#### B2. [Prioridade 2 — Arquitetura e Manutenção] Extração de `ConnectionPersistenceService` e `ImportExportService` em subcomponentes menores e redução de `MainViewModel`
- **Problema encontrado e evidência**: Embora a versão 1.7.0 tenha extraído `ConnectionEditWorkspace`, `ConnectionListQuery` e `ConnectionPlanBuilder`, [`MainViewModel.cs`](src/ConectaTI.App/ViewModels/MainViewModel.cs) ainda concentra ~1.670 linhas (misturando estado de formulário, comandos de credenciais, diagnóstico de rede e mensagens de status) e [`ImportExportDialog.xaml.cs`](src/ConectaTI.App/Views/ImportExportDialog.xaml.cs) concentra regras de apresentação e coordenação da prévia no code-behind.
- **Impacto no uso**: Aumenta o custo de manutenção ao evoluir o formulário de conexão ou a tela de importação/exportação.
- **Melhoria proposta**: Extrair um `ImportExportViewModel` dedicado para `ImportExportDialog` (permitindo testes sem instanciar controles WPF) e separar a seção de diagnóstico/status de `MainViewModel` em um colaborador coeso.
- **Esforço e risco aproximados**: **Médio** | Risco **Baixo** (coberto pela suíte de 290 testes).
- **Critério de conclusão**: `ImportExportDialog.xaml.cs` restrito a eventos de janela/diálogo de arquivo e `ImportExportViewModel` testado unitariamente sem dependência de `Window`.

#### B3. [Prioridade 3 — Confiabilidade de Dados] Verificação periódica e rotação automática de backups locais ao iniciar
- **Problema encontrado e evidência**: Em [`SqliteRepository.cs`](src/ConectaTI.App/Data/SqliteRepository.cs), backups automáticos são criados antes de migrações de esquema ou quando acionados manualmente na aba **Importação e backup**. Se o usuário passar meses apenas editando conexões sem migração de esquema nem clique manual em "Criar backup", não há ponto de restauração recente na pasta `backups/`.
- **Impacto no uso**: Em caso de exclusão definitiva acidental de vários acessos pelo usuário, o último backup em `backups/` pode ser antigo.
- **Melhoria proposta**: Criar um snapshot automático leve do `sqlite.db` (ex.: 1 backup diário na primeira gravação do dia, mantendo os últimos 10 arquivos em `backups/` com `PRAGMA integrity_check` prévio), configurável na aba **Importação e backup**.
- **Esforço e risco aproximados**: **Baixo** | Risco **Baixo** (reutiliza a rotina de backup online já existente em `SqliteRepository`).
- **Critério de conclusão**: Ao salvar a primeira alteração do dia, um backup datado é gerado em `backups/` sem bloquear a UI, respeitando o limite de retenção configurado.

#### B4. [Prioridade 4 — Segurança e Operação] Histórico local de auditoria de operações sensíveis (sem registrar segredos)
- **Problema encontrado e evidência**: [`AppLogger.cs`](src/ConectaTI.App/Services/Diagnostics/AppLogger.cs) registra eventos técnicos e falhas sanitizadas em `logs/`, mas o usuário não dispõe de uma visualização simples de quando uma conexão teve sua senha alterada, removida ou quando um pacote `.ctienc` foi exportado.
- **Impacto no uso**: Dificulta rastrear alterações acidentais de cadastro ou de credencial feitas no próprio equipamento.
- **Melhoria proposta**: Exibir no rodapé do formulário (ou na seção avançada do acesso) a data/hora da última alteração do cadastro e da credencial (`UpdatedAt` já existe em `ConnectionEntry` e `CredentialRecord`), sem armazenar nem exibir qualquer conteúdo sensível.
- **Esforço e risco aproximados**: **Muito Baixo** | Risco **Muito Baixo** (os campos `CreatedAt` e `UpdatedAt` já existem no banco).
- **Critério de conclusão**: O painel de detalhes mostra de forma discreta "Última atualização: dd/MM/yyyy HH:mm" atualizada após cada commit explícito.
