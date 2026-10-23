# ConectaTI 1.2.1 — notas técnicas e validação

**Desenvolvimento e manutenção: Max Silva.**

Versão de 22/10/2026, Windows x64, .NET SDK 10.0.402. As correções preservam a arquitetura existente, sem novas dependências de produção. A publicação é gerada em uma pasta separada das versões anteriores.

## Causas confirmadas

| Problema | Evidência e causa | Correção |
| --- | --- | --- |
| Lista desaparece e seleção diverge do formulário | Reproduzido com o código anterior compilado e uma `ListBox` WPF vinculada. O caminho `ConfirmCanLeaveCurrentEdit` executava todo o salvamento em `Task.Run` e bloqueava com `GetAwaiter().GetResult()`. Durante a recarga, `FilteredConnections.Clear()` alterava a coleção em outra thread; o WPF lançava `NotSupportedException` depois de esvaziar a origem. A seleção já tinha mudado e o formulário ainda representava o registro anterior. O SQLite continuava com os dois registros. | Remoção do salvamento da navegação e da espera bloqueante. Persistência executada fora da UI, com publicação dos resultados na UI. A lista é preparada integralmente e substituída sob proteção dos eventos. |
| Eventos recursivos e estado intermediário | `LoadSelectedIntoViewer`, reconstrução da navegação e outros caminhos encerravam a proteção com `false`, perdendo o estado recebido em chamadas aninhadas. | Preservação do estado anterior com `try/finally`; descarte de respostas de recarga/revelação antigas por geração e identidade. |
| Campos recentes ou outro formulário sobrescritos ao terminar um save | A rotina anterior consultava `EditingEntry` e campos correntes depois de vários `await`, sem garantir que ainda pertenciam ao mesmo acesso. | Captura de ID, cópia dos valores e revisões antes do primeiro `await`. A conclusão atualiza a base do rascunho salvo e conserva edições posteriores e o formulário de outro acesso. |
| Pendência inconsistente | A maioria dos campos de `ConnectionEntry` não notificava mudanças. Novos registros eram considerados sujos mesmo vazios; a senha digitada sempre era considerada uma alteração. | Propriedades observáveis, comparação com a base persistida, intenção explícita de senha e revisões por rascunho. Reverter valores retira a pendência. |
| Apagar senha mantém indicação de senha gravada | Apagar o campo não marcava remoção. A interface também limpava os campos quando recebia notificações genéricas de edição, confundindo atualização programática com edição humana. | Sincronização dos controles sob proteção e caminho explícito para edição humana; apagar uma senha efetivamente revelada ou usar Remover gera intenção de remoção. |
| Risco de perda de credenciais e alteração de outros acessos | A exclusão do segredo e do registro da credencial precedia a gravação da conexão. Operações relacionadas usavam transações separadas. A detecção de compartilhamento considerava só acessos ativos, ignorando a lixeira. Retornos de falha do cofre eram ignorados. | Substituição sempre cria credencial dedicada. Credencial e vínculo são gravados na mesma transação SQLite. A exclusão exige ausência de referências, incluindo lixeira. Falhas do cofre passam a ser detectadas. |
| Filtros não aplicados | A implementação inspecionada aplicava busca e modos de visão, mas não aplicava cliente, grupo, ambiente ou protocolo ao resultado. | Aplicação desses filtros e preservação dos critérios durante o salvamento pontual. |

### Reprodução comparativa do desaparecimento

O programa `tools/ConectaTI.Repro` usa dois registros fictícios, Dispatcher STA e uma `ListBox` vinculada. A mesma sequência foi executada contra o código anterior recompilado e contra o código corrigido:

```text
Antes: items=2, selectedMatchesForm=True
Navegação: NotSupportedException — CollectionView não aceita alterações de outra thread
Depois: sourceItems=0, prompts=1, selectedMatchesForm=False
sqliteRows=2

Corrigido: items=2, selectedMatchesForm=True
Navegação: completed
Depois: sourceItems=2, prompts=0, selectedMatchesForm=True
sqliteRows=2
```

Saídas completas: `artifacts/repro-before.txt` e `artifacts/repro-fixed.txt`. A reprodução confirma esse mecanismo no código anterior; não foi fornecido o arquivo do vídeo para reproduzir sua sequência exata.

## Comportamento entregue

- O cadastro só é gravado por Salvar ou Ctrl+S. Navegação e Conectar não gravam campos pendentes. Conectar usa os valores persistidos do ID selecionado; o registro de último acesso continua funcionando.
- Rascunhos ficam apenas em memória por ID, inclusive senhas pendentes. Novos registros têm GUID próprio e seletor de novos rascunhos. Nenhum rascunho é escrito em arquivo.
- O botão fica desabilitado sem alterações, âmbar com “Salvar alterações” quando pendente e mostra “Salvando...” durante a operação. Há aviso textual para salvar antes de fechar.
- Falha de validação ou de gravação mantém a edição para nova tentativa. Descartar restaura o valor persistido; durante uma gravação submetida, Descartar fica temporariamente indisponível para evitar uma corrida com sua conclusão.
- Fechar não confirma nem salva os rascunhos. As preferências de janela continuam sendo persistidas.
- A intenção de senha distingue manter, substituir e remover. Os quatro indicadores solicitados foram implementados. Mostrar/ocultar e limpezas programáticas não geram pendência.
- Não há recarga de toda a base após um salvamento individual. Uma recarga explícita prepara os resultados antes da publicação e mantém os dados já carregados em falha de leitura.
- A busca por digitação e os manipuladores existentes de Ctrl+N/Ctrl+S foram preservados.
- `sqlite.db` permanece portátil; importação AutoPuTTY e migração de bases legadas foram mantidas.

## Coordenação SQLite e cofre Windows

1. Uma substituição cria um novo ID, criptografa via DPAPI e registra uma instrução durável de limpeza antes de escrever no cofre. A credencial anterior continua válida.
2. Falha ao preparar o cofre impede o commit e mantém o rascunho pendente. Falha SQLite desfaz a credencial nova e o vínculo na mesma transação; a cópia preparada no cofre é removida ou fica em recuperação pendente.
3. No commit, a credencial nova e a conexão são gravadas juntas. A instrução de limpeza da credencial nova é removida. A anterior só é retirada do SQLite quando nenhuma conexão, ativa ou na lixeira, ainda a referencia.
4. O segredo antigo é retirado do cofre depois do commit. Se essa limpeza falhar, o vínculo já salvo continua correto e o usuário vê uma indicação discreta. A limpeza fica em `PendingVaultCleanup` e é tentada novamente ao iniciar ou após outra gravação.

A migração 5 adiciona somente essa fila de recuperação, com IDs e sem senhas. A transação SQLite não é tratada como uma transação do Windows. Nenhuma senha é registrada em logs ou diagnósticos adicionados.

## Arquivos de código alterados ou adicionados

- `src/ConectaTI.App/ViewModels/MainViewModel.cs`: seleção, filtros, sessões, concorrência, salvamento manual e apresentação.
- `src/ConectaTI.App/Models/ConnectionEntry.cs`: notificações dos campos editáveis.
- `src/ConectaTI.App/Models/ConnectionEditSession.cs`: rascunho em memória e intenção de senha.
- `src/ConectaTI.App/Services/ConnectionPersistenceService.cs`: coordenação da gravação e recuperação do cofre.
- `src/ConectaTI.App/Data/SqliteRepository.cs`: transação conjunta, migração 5 e fila de recuperação.
- `src/ConectaTI.App/Security/CredentialVaultService.cs`: preparação sem escrita no cofre, escrita explícita e falhas observáveis.
- `src/ConectaTI.App/Security/WindowsCredentialVaultInterop.cs`: detecção de falhas de exclusão, com ausência tratada de forma idempotente.
- `src/ConectaTI.App/MainWindow.xaml` e `.xaml.cs`: botão, aviso, rascunhos novos, sincronização da senha e fechamento sem confirmação.
- `src/ConectaTI.App/Views/ConnectionEditorDialog.xaml.cs`: consistência da remoção de senha e fechamento do editor.
- `src/ConectaTI.App/Services/Diagnostics/AppDiagnosticService.cs`, `App.xaml.cs` e `ConectaTI.App.csproj`: versão 1.2.1.
- `tests/ConectaTI.Tests/UnitTest1.cs`: expectativas do fluxo manual e execução WPF assíncrona com Dispatcher ativo.
- `tests/ConectaTI.Tests/ManualEditingRegressionTests.cs`, `EditorConcurrencyEdgeTests.cs`, `StaTest.cs` e `WpfEditorScenarios.cs`: regressões e cenário WPF real.
- `tools/ConectaTI.Repro/Program.cs` e `.csproj`: reprodução isolada com dados fictícios.
- `tools/ConectaTI.UiSmoke/Program.cs` e `.csproj`: iniciador da janela real com dados fictícios em pasta própria, sem executar a descoberta de bases legadas.
- `README.md`, este relatório e o `LEIA-ME.txt` da publicação: documentação.

O código e seu histórico estão em [MaxBSilva/ConectaTI](https://github.com/MaxBSilva/ConectaTI). Diffs e resultados de validação são artefatos locais, separados dos fontes versionados.

## Compilação e testes executados

O `dotnet` do PATH continha apenas o runtime. Foi utilizado o SDK já instalado em `$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe`, versão **10.0.402**.

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build ConectaTI.slnx -c Release --nologo

$env:CONECTATI_SCREENSHOT_PATH = Join-Path (Get-Location) 'artifacts\visual\editor-pending.png'
& $dotnet test ConectaTI.slnx -c Release --nologo `
  --logger 'trx;LogFileName=final.trx' `
  --results-directory artifacts/test-results --blame-hang-timeout 60s

& $dotnet publish src/ConectaTI.App/ConectaTI.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o dist/win-x64-portatil-v1.2.1 --nologo
```

- Compilação Release concluída sem erros ou avisos.
- Última suíte completa: **111 testes aprovados, 0 falhas, 0 ignorados**, aproximadamente 14 segundos. Resultado em `artifacts/tests/acceptance.trx`. A compilação precedente teve 0 erros e 0 avisos.
- Comandos dessa última execução: `dotnet build ConectaTI.slnx -c Release` e `dotnet test ConectaTI.slnx -c Release --no-build --blame-hang-timeout 45s --logger 'trx;LogFileName=acceptance.trx' --results-directory artifacts/tests`, usando o SDK local indicado acima.
- Incluídos testes de rascunhos A/B, novo registro sem duplicação, reverter e descartar, navegação/edição durante save, senha alterada novamente durante commit, recarga antiga após seleção nova, preservação de filtros/abas, compartilhamento com a lixeira, falha SQLite com trigger e rollback, falhas simuladas de escrita/exclusão no cofre e recuperação durável.
- O teste nativo do cofre criou, leu e removeu exclusivamente uma credencial fictícia com GUID próprio. Nenhuma credencial pessoal foi usada.
- O teste WPF instanciou as telas em STA, exercitou bindings reais, edição/reversão, rascunho de senha entre seleções e oito ciclos de salvar/trocar com uma `ListBox` vinculada. Assertou contagem, seleção, formulário, botão habilitado e cor âmbar.
- Renderização WPF produzida e inspecionada: `artifacts/visual/editor-pending.png` (1366 × 768, dados fictícios).
- Validação interativa adicional com o iniciador `tools/ConectaTI.UiSmoke`: botão inicialmente neutro/desabilitado; edição com destaque âmbar e aviso textual; Alpha → Beta → Alpha recuperando o rascunho correto; Ctrl+S persistindo a alteração e conservando as duas linhas; alternância das abas; busca incremental por `b` selecionando Beta; Ctrl+N abrindo formulário vazio sem pendência; digitação de `z` no nome sem iniciar busca; fechamento com rascunho sem popup. O iniciador foi compilado com `dotnet build tools/ConectaTI.UiSmoke -c Release -o artifacts/ui-smoke`.
- A reprodução comparativa foi repetida com `artifacts/repro-before/ConectaTI.Repro.exe` e `dotnet run --project tools/ConectaTI.Repro -c Release`: versão anterior terminou com 0 itens na coleção e exceção de thread; a versão corrigida manteve 2 itens, seleção correspondente e 0 confirmações. Ambas preservaram os 2 registros no SQLite.
- Publicação win-x64 autossuficiente concluída; a pasta nova contém somente o executável e o texto de uso, sem banco, configurações, backups, logs ou PDB.

As primeiras tentativas de teste não foram todas bem-sucedidas: o teste WPF antigo bloqueava a thread STA durante inicialização assíncrona; houve também um bloqueio de DLL por uma execução de testes preexistente. O teste foi ajustado para usar Dispatcher ativo e as execuções intermediárias foram feitas em saída separada. Duas expectativas intermediárias (troca direta do objeto de edição e localização fixa do ícone) também foram corrigidas. Esses ensaios não são apresentados como aprovações; o resultado acima corresponde à suíte final completa.

## Publicação e atualização

- Executável: `dist/win-x64-portatil-v1.2.1/ConectaTI.exe`.
- ZIP: `dist/ConectaTI-v1.2.1-win-x64-portatil.zip`.
- Conteúdo do ZIP conferido: somente `ConectaTI.exe` (versão 1.2.1.0) e `LEIA-ME.txt`. SHA-256 do ZIP: `789AA7683E6856BB24BC08CFB5E5CFF8E6618CEDDF87421CC8741232FA1006CD`.
- Hashes SHA-256: `dist/SHA256-v1.2.1.txt`. O conteúdo do ZIP foi verificado: somente `ConectaTI.exe` e `LEIA-ME.txt`.
- O pacote não inclui os dados existentes. Para atualizar uma instalação, faça backup e substitua somente seu executável, preservando `sqlite.db`, `settings.json`, backups e demais arquivos do usuário. O caminho personalizado existente continua respeitado.

## Limites da validação

- A sequência exata do vídeo não foi testada porque o vídeo não foi anexado; o defeito de coleção/thread foi reproduzido separadamente com dados fictícios.
- A renderização, testes WPF e validação interativa acima usaram dados fictícios. Ainda falta conferir teclado físico, dimensões mínimas, DPI e múltiplos monitores na configuração do usuário.
- Os testes existentes de launchers e AutoPuTTY passaram. Não foram feitas conexões autenticadas a servidores reais nem importação do arquivo pessoal do usuário.
- Os bancos originais do usuário foram preservados. Os testes funcionais, reabertura e credenciais usaram bases fictícias temporárias; o uso com os dados reais permanece uma verificação operacional após backup.
- O ZIP e o executável foram gerados, mas não foi executada uma instalação pessoal a partir do ZIP. A validação funcional usou o projeto compilado e o harness WPF.
