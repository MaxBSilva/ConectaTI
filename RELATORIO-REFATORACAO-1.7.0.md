# ConectaTI 1.7.0 — refatoração estrutural

Entrega de 04/11/2026, com referência funcional na versão 1.6.0. AGENTS.md, CONTEXTO.md e RELATORIO-ENTREGA-1.6.0.md foram lidos antes das alterações. A suíte da cópia recebida foi executada antes da primeira extração: **257 executados, 257 aprovados**, zero ignorados; evidência `artifacts/test-results/refactor-baseline-1.6.0.trx`.

## Responsabilidades extraídas

1. **Sessões e revisões:** ConnectionEditWorkspace mantém sessões por ID, ativação/geração e novos rascunhos. ConnectionEditSession passa a controlar os incrementos de revisão, intenção da senha e reconciliação do commit por ConnectionSaveSubmission. O ViewModel continua responsável pelas notificações/eventos WPF e restaura proteções em finally.
2. **Lista:** ConnectionListQuery reúne busca sem acentos, filtros, ordenação, seleção por ID e limites do movimento na ordem exibida. ConnectionListNavigation permanece o adaptador de foco/virtualização WPF. Inativos continuam na lista e na pesquisa.
3. **Credenciais e conclusões assíncronas:** EditorOperationGuard centraliza identidade do objeto, IDs, geração e revisões. ConnectionCredentialService separa leitura/cópia dos adaptadores SQLite/DPAPI/clipboard. PasswordRevealLease concentra sequência, timer e cancelamento da apresentação e é compartilhado pelo editor e gerenciador de credenciais.
4. **Banco:** SqliteSchemaMigrator contém a evolução 1–10, backup prévio e transações. O repositório conserva CRUD, backups e operações de importação/credenciais. O SQL e a ordem das migrações foram preservados; não há migração nova.
5. **Formatos:** codecs próprios de CSV, XML e JSON extraídos da coordenação de importação. Sintaxe CSV, mapeamento dos campos, aliases e conversões são explícitos; a prévia, classificação de duplicatas e commit protegido continuam no serviço. Contratos de intercâmbio foram separados mantendo os nomes serializados.
6. **Conexões externas:** ConnectionPlanBuilder/CommandLineTokenizer constroem conteúdo RDP e argumentos; ConnectionLauncherService coordena execução e histórico. IProcessRunner e IRdpPasswordEncoder permitem substituir processo e codificação nativa sem iniciar clientes. As proteções de concorrência TERMSRV, IDs de limpeza e nomes legíveis RDP permanecem.

As interfaces foram limitadas a fronteiras exercitadas: lookup/leitura de credenciais, clipboard, processo, codificação RDP e probe DNS/TCP. Nenhuma dependência de produção foi adicionada. Não houve criação de novos arquivos partial como solução das extrações.

## Clareza, consistência e cenário descoberto

- Removidos o snapshot privado nunca lido do ViewModel, a referência não utilizada ao clipboard no launcher, o cálculo de fullAddress sem uso e um ramo de bloqueio impossível porque IsLocked é sempre false nesta versão. APIs anteriores necessárias à compatibilidade/testes continuam.
- Nomes internos distinguem sessão corrente, registros fora da lixeira e registros na lixeira. Os nomes legados ActiveConnectionsCount/GetActiveCountAsync foram preservados, mas o código novo e os textos do diagnóstico esclarecem “fora da lixeira”. O alias novo não altera a representação JSON anterior.
- A nova fronteira de diagnóstico permitiu reproduzir um defeito: A concluía após selecionar B e preenchia status/painel de B; uma falha atrasada também escapava do comando. **Os dois testes falharam antes da proteção** (`refactor-diagnostic-repro.trx`). O guard compartilhado agora descarta esses resultados e trata cancelamento/erro atual. É uma correção de isolamento encontrada nesta etapa, não uma alteração de classificação de rede.
- A leitura de senha continua sem modificar o cadastro. A cópia completa conserva o comportamento de omitir uma senha vazia, enquanto cópia individual/revelação continuam tratando leitura vazia como ilegível. Nenhum resultado de teste ou diagnóstico contém a senha fictícia.

## Contratos e apresentação preservados

Salvar/Ctrl+S permanece o único commit de cadastro. Navegação, novo rascunho, conexão e fechamento não salvam nem pedem confirmação das pendências. Permanecem rascunhos por ID, edições posteriores à submissão, manter/substituir/remover senha, proteção CurrentUser/DPAPI, credenciais compartilhadas, recuperação, backups, AutoPuTTY, formatos próprios, status Ativo/Inativo, navegação por teclado e identificação RDP.

Os XAML de produção e as propriedades/ordinais serializados de ConnectionEntry/AppSettings são idênticos à referência preservada. Os painéis continuam lado a lado. Os textos de contagem foram esclarecidos, sem reformulação da apresentação. A arquitetura atual e seus limites estão em [ARQUITETURA.md](ARQUITETURA.md).

| Arquivo principal | Linhas na referência | Linhas após extração |
| --- | ---: | ---: |
| MainViewModel.cs | 1666 | 1502 |
| SqliteRepository.cs | 1379 | 1025 |
| ImportExportService.cs | 1183 | 654 |
| ConnectionLauncherService.cs | 654 | 317 |

A redução de linhas não é o critério de validação: as responsabilidades têm componentes e testes próprios. O ViewModel ainda contém fachada/presentação e coordenação; as regras extraídas continuam no mesmo assembly net10.0-windows. Não foi criado um projeto Core independente nem reescrito o armazenamento.

## Git: causa, recuperação e preservação

O HEAD local era o commit `4de6b0c90a49656d8efe02a65effbef1826b842b`, apontando para a árvore ausente `a690f190e3b01435217acbde426328d0fbff1718` e um ancestral ausente. fsck também encontrou blobs/árvores ausentes e cache-tree quebrado no índice. A causa técnica é um banco de objetos incompleto; a origem física dessa perda não foi comprovada.

Antes de editar: cópia de fontes/testes/tools/documentos e de toda a pasta .git em `artifacts/refactor-baseline-1.6.0`, sem bancos ou configurações pessoais. Um clone remoto bare separado foi verificado com fsck; nele existiam os objetos necessários. Somente seus objetos foram incorporados à pasta .git local. Nenhum reset, restore, checkout de fontes, stash, limpeza ou garbage collection foi executado.

HEAD e seus ancestrais agora têm zero objetos ausentes na verificação de alcançabilidade; status e diff funcionam. Branch criada: `codex/refactor-1.7.0`. fsck completo ainda registra árvores antigas desconectadas incompletas, sem versão recuperável no remoto consultado. Foram mantidas, junto ao backup, para eventual recuperação adicional. As alterações locais anteriores permanecem; não houve commit nem push do conjunto misto. O .gitignore ausente foi recomposto para excluir dados, artefatos e distribuições.

## Verificações realizadas nesta execução

| Verificação | Resultado e evidência |
| --- | --- |
| Referência antes de editar | 257/257, `refactor-baseline-1.6.0.trx` |
| Extração de migrações | 76/76, `refactor-migrations.trx` |
| Extração do editor | 49/49, `refactor-editor.trx` |
| Extração de lista | 28/28, `refactor-list.trx` |
| Extração de credenciais | 49/49, `refactor-credentials.trx` |
| Extração de planos/adaptadores | 51/51, `refactor-launcher.trx` |
| Extração de codecs | 71/71, `refactor-codecs.trx` |
| Integração intermediária | 284/284, `refactor-integration.trx` |
| Reprodução do diagnóstico obsoleto | 2 falhas esperadas antes da correção, `refactor-diagnostic-repro.trx` |
| Compilação final Release | Sucesso, zero avisos e erros, via tools/Publish-Release.ps1 |
| Suíte final completa | **287 executados, 287 aprovados, zero falhas e zero ignorados**, `release-1.7.0.trx` |

Os TRX estão em `artifacts/test-results`. Os 257 testes existentes foram preservados e 30 casos foram acrescentados. As novas fronteiras cobrem isolamento/revisões, reconciliação após save, parâmetros de invalidação, ordenação/limites, timer, leitura atrasada/falha/cancelamento, clipboard substituído, planos sem execução, processo simulado com histórico somente após sucesso, rollback/retry de migração, semântica das contagens e compatibilidade dos codecs. A suíte inteira conserva as regressões anteriores de senha, compartilhamento, importação, rollback e WPF.

Verificações Windows adicionais:

- Janela real pelo iniciador UiSmoke, com pasta temporária nova e dados fictícios: rascunhos A/B e nova identidade, cópia explícita de campos recolhidos/alterações não salvas, salvar sem alterar B, credencial fictícia DPAPI, prévia RDP sem segredos, inativar/reativar, painéis lado a lado em fonte 11/18, temas claro/escuro e descarte sem persistência automática. Clipboard substituído apenas nesse modo de teste. Resultado: `tools/ConectaTI.UiSmoke/bin/Release/net10.0-windows/refactor-checks.json`.
- Navegação na janela WPF com lista virtualizada, busca com acentos, setas, pausa, seleção distante, limites, filtros, Search, seleção estendida e foco nos campos: nova execução aprovada com `--navigation --navigation-checks --narrow --font=18 --dark`. Marcador `navigation-checks.txt` na saída do iniciador.
- Executável final 1.7.0 aberto com `--data-dir` na fixture temporária. Inspeção visual em tema escuro/fonte 18: painéis lado a lado e indicação Inativo/PROD; ↓ moveu Alpha para Beta e ↑ retornou a Alpha, com nome/endereço/status/senha protegida acompanhando a seleção. Nenhuma credencial foi revelada na inspeção manual.

## Publicação

Comando executado: `.\tools\Publish-Release.ps1 -Dotnet "$env:LOCALAPPDATA\ConectaTI-Development\dotnet\dotnet.exe"`, SDK 10.0.403 no Windows. Nova pasta `dist/ConectaTI-1.7.0-win-x64`, somente ConectaTI.exe e release-manifest.json. Arquivo 1.7.0.0, Windows x64 autocontido, esquema 10.

Tamanho: 67032912 bytes. SHA-256: `DC1D67E2E8DC833A803563928DEB4BB7D73E22FBB6BE6943A9996E91D3FA2FBC`.

A pasta 1.6.0 foi preservada; seu executável continua com SHA-256 `48A56E3361E046E4ABDB31AF0E7651819A16266FDE489E773AE189C8901DEE38`, conferido contra a cópia de referência inicial. Banco, configurações, credenciais, logs e backups pessoais não foram incluídos na publicação.

## Arquivos alterados nesta etapa

Inventário relativo à cópia funcional 1.6.0, não ao HEAD histórico que já tinha alterações locais. Hashes por arquivo em `artifacts/refactor-1.7.0-files.json`.

- Novo: `.gitignore`.
- Novo: `ARQUITETURA.md`.
- Alterado: `CONTEXTO.md`.
- Alterado: `README.md`.
- Novo: `RELATORIO-REFATORACAO-1.7.0.md`.
- Alterado: `src/ConectaTI.App/App.xaml.cs`.
- Alterado: `src/ConectaTI.App/ConectaTI.App.csproj`.
- Alterado: `src/ConectaTI.App/Data/SqliteRepository.cs`.
- Novo: `src/ConectaTI.App/Data/SqliteSchemaMigrator.cs`.
- Novo: `src/ConectaTI.App/Editing/ConnectionEditWorkspace.cs`.
- Novo: `src/ConectaTI.App/Editing/EditorOperationGuard.cs`.
- Novo: `src/ConectaTI.App/Editing/PasswordRevealLease.cs`.
- Alterado: `src/ConectaTI.App/Helpers/ConnectionListNavigation.cs`.
- Alterado: `src/ConectaTI.App/Localization/Strings.en.resx`.
- Alterado: `src/ConectaTI.App/Localization/Strings.es.resx`.
- Alterado: `src/ConectaTI.App/Localization/Strings.resx`.
- Alterado: `src/ConectaTI.App/Models/ConnectionEditSession.cs`.
- Alterado: `src/ConectaTI.App/Security/ClipboardService.cs`.
- Novo: `src/ConectaTI.App/Security/ConnectionCredentialService.cs`.
- Novo: `src/ConectaTI.App/Security/CredentialBoundaries.cs`.
- Alterado: `src/ConectaTI.App/Security/CredentialVaultService.cs`.
- Novo: `src/ConectaTI.App/Services/Connections/ConnectionListQuery.cs`.
- Alterado: `src/ConectaTI.App/Services/Diagnostics/AppDiagnosticService.cs`.
- Novo: `src/ConectaTI.App/Services/Diagnostics/INetworkConnectivityProbe.cs`.
- Alterado: `src/ConectaTI.App/Services/Diagnostics/NetworkDiagnosticService.cs`.
- Novo: `src/ConectaTI.App/Services/ImportExport/ConnectionCsvCodec.cs`.
- Novo: `src/ConectaTI.App/Services/ImportExport/ConnectionJsonCodec.cs`.
- Novo: `src/ConectaTI.App/Services/ImportExport/ConnectionTransferValues.cs`.
- Novo: `src/ConectaTI.App/Services/ImportExport/ConnectionXmlCodec.cs`.
- Novo: `src/ConectaTI.App/Services/ImportExport/CsvRecordCodec.cs`.
- Alterado: `src/ConectaTI.App/Services/ImportExport/ImportExportService.cs`.
- Novo: `src/ConectaTI.App/Services/ImportExport/ImportModels.cs`.
- Novo: `src/ConectaTI.App/Services/Launchers/CommandLineTokenizer.cs`.
- Alterado: `src/ConectaTI.App/Services/Launchers/ConnectionLauncherService.cs`.
- Novo: `src/ConectaTI.App/Services/Launchers/ConnectionPlanBuilder.cs`.
- Novo: `src/ConectaTI.App/Services/Launchers/WindowsLaunchAdapters.cs`.
- Alterado: `src/ConectaTI.App/ViewModels/MainViewModel.Batch.cs`.
- Alterado: `src/ConectaTI.App/ViewModels/MainViewModel.CopyAll.cs`.
- Alterado: `src/ConectaTI.App/ViewModels/MainViewModel.cs`.
- Alterado: `src/ConectaTI.App/ViewModels/MainViewModel.Language.cs`.
- Alterado: `src/ConectaTI.App/ViewModels/MainViewModel.Rdp.cs`.
- Alterado: `src/ConectaTI.App/Views/CredentialsDialog.xaml.cs`.
- Alterado: `src/ConectaTI.App/Views/ExecutablesSettingsDialog.xaml.cs`.
- Novo: `tests/ConectaTI.Tests/RefactorBoundaryTests.cs`.
- Novo: `tests/ConectaTI.Tests/RefactorDiagnosticRaceTests.cs`.
- Novo: `tests/ConectaTI.Tests/RefactorEditingAndListTests.cs`.
- Alterado: `tools/ConectaTI.UiSmoke/Program.cs`.
- Novo: `tools/ConectaTI.UiSmoke/RefactorChecks.cs`.

## Limites reais

Nenhuma sessão remota autenticada em MSTSC, SSH, WinSCP ou VNC foi homologada. Construção de plano, execução substituída e abertura de aplicativo não comprovam autenticação, nem título/barra de tarefas de uma sessão RDP autenticada. As combinações DPI 125%/150% e múltiplos monitores não foram homologadas. Os objetos Git antigos desconectados mencionados acima continuam incompletos. A revisão visual ampla permanece para a fase seguinte; não houve redesign nesta entrega.
