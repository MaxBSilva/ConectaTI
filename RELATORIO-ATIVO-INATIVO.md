# Status Ativo/Inativo — 03/11/2026

## Uso e comportamento

Ao lado do nome do acesso, a caixa de alternância mostra **Ativo** quando marcada e **Inativo** quando desmarcada. A alteração pertence ao formulário atual e só é gravada com **Salvar / Ctrl+S**. Navegar mantém os rascunhos separados por ID; fechar não salva alterações pendentes nem pede confirmação.

Na lista, um acesso inativo recebe nome vermelho, faixa vermelha e selo textual **Inativo**. O ambiente PROD/TESTE permanece visível. Na seleção, o nome usa uma cor ajustada ao fundo selecionado e os indicadores vermelhos continuam visíveis. A aparência normal retorna após salvar a reativação. A lista representa o estado persistido; a chave do painel representa o rascunho, até o salvamento.

Não há filtro, alteração de ordem ou bloqueio de conexão por inatividade. Os registros permanecem disponíveis em pesquisas, favoritos, navegação por setas, consulta, edição e cópia. A cópia completa também inclui o status. Lixeira e ambiente permanecem independentes.

## Persistência e compatibilidade

Migração SQLite **10** acrescenta IsActive, inteiro 0/1, default **1**, em transação. O mecanismo existente cria backup consistente antes de migrar bases antigas. Registros antigos, inclusive os da lixeira, passam a ativos por padrão; novos cadastros e importações sem status também começam ativos. Clone, duplicação e comparação do rascunho incluem o status.

Backup/restauração SQLite, JSON, XML próprio, CSV próprio e pacotes .ctienc/.ctishare preservam o status. No CSV, a coluna é **Ativo** (Sim/Não); XML e JSON usam **IsActive**. Cabeçalhos CSV IsActive/Active também são reconhecidos; 0 é inativo e 1 é ativo. Foi corrigido o repasse da coluna de status na etapa de mapeamento do CSV da interface, preservando também os metadados RDP desse fluxo.

Os formatos externos RDP/AutoPuTTY não recebem propriedades inventadas para representar este status do aplicativo. Exportar para esses formatos não transporta a classificação Ativo/Inativo.

## Arquivos alterados

- src/ConectaTI.App/Models/ConnectionEntry.cs: estado observável, rótulo, clone e comparação.
- src/ConectaTI.App/Data/SqliteRepository.cs: migração 10, seleção e gravação.
- src/ConectaTI.App/MainWindow.xaml: alternância perto do nome e indicadores na lista.
- src/ConectaTI.App/App.xaml e Services/ThemeService.cs: cor de nome inativo selecionado para cada tema.
- src/ConectaTI.App/Services/ConnectionCopyFormatter.cs: status na cópia completa.
- src/ConectaTI.App/Services/ImportExport/ImportExportService.cs: status nos formatos próprios.
- src/ConectaTI.App/Views/ImportExportDialog.xaml.cs: repasse dos mapeamentos de status/RDP.
- src/ConectaTI.App/Localization/Strings.resx, Strings.en.resx e Strings.es.resx: estados e esclarecimento do botão Todos.
- tests/ConectaTI.Tests/ActivityStatusTests.cs: nove casos de regressão.
- tests/ConectaTI.Tests/ApplicationVaultTests.cs e RdpTests.cs: versão esperada do esquema e fixture histórica.
- tools/ConectaTI.UiSmoke/Program.cs: cenário fictício --activity e dados de navegação com ambos os estados.
- README.md, CONTEXTO.md e este relatório.

## Verificações executadas

- Build Release: sucesso, sem avisos/erros.
- Suíte Release: **222 aprovados, zero falhas**. Relatório: tests/ConectaTI.Tests/TestResults/activity-final.trx.
- Nove casos novos cobrem status em rascunhos separados, salvar/reabrir/reativar, credencial compartilhada preservada e legível, cópia, pesquisa sem acento incluindo ambos os estados, migração de esquema 9 com backup e default ativo, independência da lixeira, backup/restauração, CSV/XML/JSON (incluindo legado sem status), pacotes com/sem senha e lançamento manual de inativo com processo substituído, sem modificar propriedades RDP.
- Inspeção real pela skill computer-use e ConectaTI.UiSmoke, com base fictícia isolada, janela estreita de 620 px e fonte 18: ativo/inativo, selo PROD/TESTE, contraste selecionado e não selecionado, temas claro/escuro e controle perto do nome sem corte.
- Reativação no painel habilitou Salvar; após salvar, a linha perdeu faixa/selo/vermelho. Acesso foi novamente inativado e salvo por Ctrl+S, a janela foi fechada e reaberta e o selo Inativo permaneceu.
- Search com `avicola` retornou ativo e inativo; ↓ mudou do ativo para o inativo, e ↑ retornou ao ativo com o painel sincronizado.
- Regressão WPF da navegação UiSmoke executada novamente após a alteração; resultado em tools/ConectaTI.UiSmoke/bin/Release/net10.0-windows/navigation-checks.txt.

Os testes não usaram cadastros pessoais. A conexão real com o cliente não foi realizada; o teste automatizado de lançamento substitui a criação do processo e não comprova autenticação remota. A inspeção visual não cobre todas as combinações de DPI/tamanho de janela.

Distribuição Windows x64 com runtime em **dist/ConectaTI-Ativo-Inativo/ConectaTI.exe**, sem banco ou credenciais pessoais.
