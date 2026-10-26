# Contexto técnico do ConectaTI

Aplicativo Windows x64 em C#/.NET 10, WPF/MVVM e SQLite portátil. O `README.md` descreve uso e comandos de desenvolvimento. As dependências de produção permanecem CommunityToolkit.Mvvm, Microsoft.Data.Sqlite e Konscious.Security.Cryptography.Argon2.

## Contratos de edição e persistência

- Cada cadastro possui ID estável; `ConnectionEditSession` mantém sua cópia de edição, baseline, revisões e intenção de senha em memória.
- Somente Salvar/Ctrl+S persiste os campos. Navegação, conexão, cópia, criação de rascunho e fechamento não salvam nem solicitam confirmação de alterações pendentes.
- Capturar identidade, valores e revisões antes de awaits. Uma conclusão antiga não pode substituir o formulário ou as edições posteriores de outro acesso.
- Atualizar propriedades/coleções vinculadas no Dispatcher. Os testes de WPF usam STA com Dispatcher assíncrono ativo.
- Senha protegida vazia significa manter; substituição e remoção são intenções explícitas no commit. Preservar credenciais compartilhadas, DPAPI CurrentUser e recuperação dos formatos anteriores.
- Preservar SQLite, configurações, migrações, backups e importação AutoPuTTY. Testes usam pastas próprias/dados fictícios; UiSmoke evita descoberta de bases pessoais.

## Cópia do acesso

`MainViewModel.Copy.cs` concentra as cópias de nome, endereço, usuário, senha e acesso completo. Antes do primeiro await, captura a sessão/revisões, os textos do editor, o ID da credencial e a intenção de senha. O painel em edição é a fonte única: valores vazios não são substituídos por dados antigos de outro cadastro.

A leitura usa `SqliteRepository.GetCredentialByIdAsync` e o mecanismo existente `RecoverPasswordAsync`/`CredentialVaultService.ReadSecretAsync`. Usuário vazio mantém o fallback da credencial vinculada, como na cópia individual anterior. Senha oculta é recuperada do cofre; substituição pendente usa o texto em memória; remoção pendente copia vazio. Erro de recuperação impede copiar um conjunto parcial.

O novo `CopyCompleteAccessCommand` depende de `HasSelectedConnection` e exige que o ID selecionado corresponda à sessão capturada. A cópia usa as linhas `Nome:`, `Endereço:`, `Usuário:` e `Senha:` em português, mesmo se outro idioma estiver selecionado na interface. O endereço é o texto do campo unificado, preservando portas explícitas e a apresentação existente de portas padrão/personalizadas, sem resolução DNS.

`ClipboardService.CopyTextWithRetryAsync` recebe uma verificação opcional da validade da cópia antes de cada tentativa. Trocas de sessão/geração ou edições posteriores cancelam o resultado antigo. O serviço mantém seis tentativas assíncronas limitadas e trata indisponibilidade sem encerrar a UI. Uma tentativa malsucedida não cancela a limpeza de uma senha anteriormente copiada.

A cópia completa é tratada como segredo: usa a limpeza automática existente por hash, conforme o intervalo configurado (fallback de 30 segundos). Não revela a senha na tela, não conecta e não salva. O conteúdo com senha não passa por logs nem pelo cache de mensagens de `L.Format`; somente as confirmações/falhas genéricas são traduzidas. A confirmação é `Dados do acesso copiados`.

## Layout adaptável

`MainWindow.xaml` mantém a identidade na barra de título e retira somente o logo/texto do cabeçalho interno. Um `WrapPanel` medido pela largura real acomoda os comandos superiores. Os botões de detalhes têm margens para quebra de linha, e a cópia completa ocupa uma linha própria.

`MainWorkspace` tem duas colunas permanentes: lista à esquerda, detalhes à direita e divisor vertical arrastável. Não existe disposição empilhada nem mudança de linhas ao reduzir a janela. Os mínimos funcionais são 130 DIPs para a lista e 300 para os detalhes; o mínimo da janela permanece 480 DIPs. Campos seguem a largura disponível e os painéis possuem suas próprias rolagens verticais. Em colunas estreitas ou com pouca altura, os quatro botões de visão se agrupam em `ViewModeMenuButton`, usando os mesmos comandos e o indicador do modo ativo.

`MainWindow.xaml.cs` mantém uma proporção preferida da lista em `LeftPaneRatio`. Durante um redimensionamento, aplica essa proporção respeitando os mínimos, sem substituir a preferência pelo valor temporariamente limitado. `PaneSplitter_DragCompleted` captura a escolha do usuário e `SaveWindowGeometry` grava a proporção. Configurações antigas sem proporção usam a largura anterior na primeira disposição. A estrutura conserva as mesmas instâncias dos controles, bindings e sessões.

`AppSettings.FontSize` continua independente de `Density` (`InterfaceDensity.Compact/Standard/Comfortable`). As opções de letras usam 11, 12,5 e 18 DIPs; valores personalizados antigos entre 11 e 18 permanecem legíveis e não são alterados ao trocar somente a densidade. `SettingsService.Load` converte o antigo `ComfortableMode` quando não existe `Density`; uma densidade nova explícita tem prioridade sobre o sinalizador antigo. As configurações novas gravam `Density` e `LeftPaneRatio` em JSON, sem migração do SQLite.

`ViewPreferencesService` aplica fontes e recursos de espaçamento separados: padding de campos/botões, margens, área dos detalhes, altura mínima/padding dos itens e cabeçalho. `FontSizeCombo` e `DensityCombo`, em Configurações/Geral, salvam somente a preferência correspondente. O redimensionamento nunca recalcula fontes ou densidade. Os textos dos itens e campos usam altura automática para acomodar letras maiores.

Os detalhes têm `ScrollViewer` vertical, sem rolagem horizontal. Não houve aumento das dimensões mínimas da janela nem redução global de fontes. As traduções novas estão nos três catálogos; `.gitattributes` fixa LF em `.resx` para manter equivalência entre o XML e os recursos compilados no Windows.

## Pesquisa incremental e foco

A causa do salto foi confirmada no código e reproduzida em WPF: `ExecuteIncrementalSearch` alterava a seleção via ViewModel e chamava `ScrollIntoView`, mas o `ListBoxItem` antes focado continuava sendo o ponto de partida da navegação nativa. O teste anterior à correção selecionou `Avícola (Serviços 9,10,11)` e constatou o foco no item anterior (`artifacts/side-by-side/keyboard-before-fix.trx`). O timer existente apenas limpava a busca; não havia busca assíncrona atrasada a reaplicar o resultado.

Agora a entrada usa `PreviewTextInput`, aceitando caracteres compostos/acentos, e desabilita a pesquisa textual nativa paralela com `IsTextSearchEnabled=false`. O resultado é atribuído ao `SelectedItem`, rolado/realizado e recebe foco pelo seu `ListBoxItem`. Não há callback de seleção pendente. Setas, Home/End e PageUp/PageDown limpam a busca/timer e seguem para o manipulador nativo, sem um segundo movimento implementado pelo aplicativo. Mouse e saída de foco também limpam a busca. A seleção múltipla e Ctrl/Shift continuam nativos; digitação nos campos de texto não passa pelo manipulador da lista.

`KeyboardNavigationUiScenarios` usa uma janela WPF real em STA/Dispatcher para verificar resultado/foco, Down/Up em passos únicos, acentos, buscas consecutivas/rápidas, limite inicial/final, nova âncora de seleção/foco, filtro, seleção múltipla e separação do editor. Os exemplos usam somente dados fictícios e uma ordem explícita de acessos recentes.

## Verificação

Usar `dotnet build ConectaTI.slnx -c Release` e `dotnet test ConectaTI.slnx -c Release`. `CompleteAccessCopyTests` mantém as regressões da cópia. `ResponsiveUiScenarios` agora verifica os painéis lado a lado em 1440×900, 900×650 e 480×380, com todas as nove combinações de fonte/densidade e três idiomas (81 renderizações em 96/120/144 DPI). Verifica limites, altura dos campos, área útil da lista, alcance da cópia por rolagem e persistência da proporção do divisor. A suíte conserva a matriz existente dos diálogos. `ViewPreferenceTests` cobre persistência independente das nove combinações, migração e valores inválidos.

Os artefatos de validação ficam em `artifacts/copy-responsive/`, fora do Git. Renderizar em DPI diferente não altera nem comprova o funcionamento com todas as configurações reais de escala do Windows. Resultados de cada execução devem ser informados com sua evidência, sem reutilizar contagens históricas como execução nova.

Validação anterior do ajuste de cópia: 209 aprovados e 27 renderizações em `artifacts/copy-responsive/`. Essa evidência é histórica e não representa a execução do ajuste posterior de painéis/teclado. A nova validação usa `artifacts/side-by-side/`, com os PNGs e limites em `ui/layout.json`.

Na validação anterior de cópia, também foi aberto o UiSmoke real, somente com dados fictícios: conferidos maximização, cabeçalho sem identificação duplicada, presença do botão, confirmação discreta de cópia e desabilitação ao abrir um novo rascunho. A janela isolada foi encerrada ao final. Isso não representa uma nova inspeção manual do ajuste posterior de painéis/teclado.

A execução final do ajuste de painéis/teclado, em 26/10/2026, usou SDK 10.0.402: **build Release com 0 erros/avisos; 222 testes aprovados, 0 falhas e 0 ignorados**, incluindo 13 novos casos de persistência/migração de preferências. Evidência em `artifacts/side-by-side/full-suite.trx`. A validação WPF verificou as nove combinações de fonte/densidade em três tamanhos e três idiomas (81 imagens), preservação da proporção do divisor após redimensionamento/gravação e as regressões de teclado em janela real STA/Dispatcher. Não foram alteradas as configurações de escala do Windows; 96/120/144 DPI são renderizações e não uma certificação em três configurações nativas de monitor.

A publicação local atual fica em `dist/win-x64-portatil-v1.5.0-paineis-teclado/ConectaTI.exe`, separada das publicações anteriores e sem dados pessoais. A pasta `win-x64-portatil-v1.5.0-interface` é da entrega anterior.

## Arquivos do ajuste de painéis, preferências e teclado

- `src/ConectaTI.App/MainWindow.xaml` e `.xaml.cs`: colunas permanentes, divisor proporcional, menu de visões e sincronização da pesquisa/seleção/foco.
- `src/ConectaTI.App/App.xaml` e `Services/ViewPreferencesService.cs`: fontes e espaçamentos independentes.
- `src/ConectaTI.App/Models/AppSettings.cs`, `Models/Enums.cs` e `Services/SettingsService.cs`: preferências, proporção e compatibilidade do JSON anterior.
- `src/ConectaTI.App/Views/ExecutablesSettingsDialog.xaml` e `.xaml.cs`: seletores de letras e densidade.
- `src/ConectaTI.App/ViewModels/MainViewModel.cs` e `MainViewModel.Batch.cs`: rótulo/atualização do modo de visão no menu compacto, preservando o comando existente.
- `src/ConectaTI.App/Localization/Strings.resx`, `Strings.en.resx` e `Strings.es.resx`: traduções das opções.
- `tests/ConectaTI.Tests/KeyboardNavigationUiScenarios.cs`, `ViewPreferenceTests.cs`, `ResponsiveUiScenarios.cs`, `ImprovementsUiScenarios.cs` e `UnitTest1.cs`: regressões de navegação, persistência e layout WPF.
- `README.md` e `CONTEXTO.md`: comportamento, arquitetura e alcance das verificações.
