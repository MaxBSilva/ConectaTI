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

`MainWindow.xaml.cs` alterna o `MainWorkspace` abaixo de 680 DIPs: lista acima dos detalhes e divisória horizontal, com quatro filtros por linha. Em larguras maiores, usa duas colunas e divisória vertical. Os painéis continuam sendo as mesmas instâncias, sem recriar controles, bindings ou sessões. A largura preferida da lista é conservada ao entrar/sair da disposição empilhada; a altura dos painéis acompanha a área disponível. Fonte grande recebe mais espaço relativo para a lista na disposição empilhada.

Os detalhes têm `ScrollViewer` vertical, sem rolagem horizontal. Não houve aumento das dimensões mínimas da janela nem redução global de fontes. As traduções novas estão nos três catálogos; `.gitattributes` fixa LF em `.resx` para manter equivalência entre o XML e os recursos compilados no Windows.

## Verificação

Usar `dotnet build ConectaTI.slnx -c Release` e `dotnet test ConectaTI.slnx -c Release`. `CompleteAccessCopyTests` cobre cópia oculta, rascunhos, troca de acesso, concorrência, endereços e falhas de recuperação/clipboard. `ResponsiveUiScenarios`, integrado ao teste WPF existente, renderiza 1440×900, 900×650 e 480×380 em 96/120/144 DPI e três idiomas, verificando limites dos comandos, painéis e alcance do botão pela rolagem. A suíte também mantém a renderização existente dos diálogos.

Os artefatos de validação ficam em `artifacts/copy-responsive/`, fora do Git. Renderizar em DPI diferente não altera nem comprova o funcionamento com todas as configurações reais de escala do Windows. Resultados de cada execução devem ser informados com sua evidência, sem reutilizar contagens históricas como execução nova.

Execução de 26/10/2026 com SDK 10.0.402: build Release com 0 erros/avisos e suíte final com **209 aprovados, 0 falhas e 0 ignorados** (`artifacts/copy-responsive/full-suite.trx`). Inclui 15 casos novos de cópia. Foram geradas/validadas 27 renderizações da janela principal (três tamanhos × três idiomas × três DPI), incluindo fonte 18 nos casos de 144 DPI, além da matriz existente dos diálogos. `artifacts/copy-responsive/ui/layout.json` registra os limites medidos e os PNGs ficam na mesma pasta.

Também foi aberto o UiSmoke real, somente com dados fictícios: conferidos maximização, cabeçalho sem identificação duplicada, presença do botão, confirmação discreta de cópia e desabilitação ao abrir um novo rascunho. A janela isolada foi encerrada ao final. As configurações reais de escala do Windows não foram alteradas; 125%/150% foram verificados por renderização, e os tamanhos intermediário/reduzido foram validados nos testes WPF, sem afirmar uma inspeção manual dessas combinações no desktop.

A publicação local desta mudança fica em `dist/win-x64-portatil-v1.5.0-interface/ConectaTI.exe`, separada das publicações anteriores e sem dados pessoais.

## Arquivos do ajuste de cópia e layout

- `src/ConectaTI.App/MainWindow.xaml` e `.xaml.cs`: novo botão, cabeçalho e adaptação dos painéis/divisórias.
- `src/ConectaTI.App/ViewModels/MainViewModel.Copy.cs` e `MainViewModel.cs`: comando completo e fluxo comum das cópias individuais, com captura e validação da sessão.
- `src/ConectaTI.App/Security/ClipboardService.cs`: verificação antes de cada tentativa e conservação da limpeza anterior quando uma cópia falha.
- `src/ConectaTI.App/Localization/Strings.resx`, `Strings.en.resx`, `Strings.es.resx` e `.gitattributes`: traduções e quebras de linha consistentes nos recursos.
- `tests/ConectaTI.Tests/CompleteAccessCopyTests.cs`, `ResponsiveUiScenarios.cs` e `UnitTest1.cs`: regressões de cópia e validação WPF integrada.
- `README.md` e `CONTEXTO.md`: comportamento, arquitetura e alcance das verificações.
