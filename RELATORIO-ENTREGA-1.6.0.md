# ConectaTI 1.6.0 — correções prioritárias

Entrega de 03/11/2026. Relatório de entrada: ConectaTI_Analise_2026-11-03.md, conferido contra o código desta pasta antes das correções. O relatório foi usado como evidência e orientação técnica; as propostas de refatoração estrutural e reformulação visual não foram implementadas nesta etapa.

## Achados confirmados e correções

| Achado no código recebido | Correção entregue |
| --- | --- |
| Cópia individual aguardava banco/cofre sem conferir novamente a seleção. Recuperação, falhas e gerenciador de credenciais também podiam atualizar a sessão seguinte. | Captura de identidade, sessão, geração e revisões antes dos awaits. Verificação antes de revelar/copiar, de cada tentativa do clipboard e das mensagens. Navegação, edição, fechamento e retorno A→B→A invalidam a operação antiga. |
| No gerenciador, cancelar o timer não impedia atribuir a senha após uma leitura antiga; salvar podia recarregar e selecionar o registro anterior. | Cancelamento e revisão conferidos imediatamente após a leitura e na ocultação. Salvar usa snapshot independente e não altera a seleção/edição posterior. |
| Propriedades RDP eram inteiros válidos no parser, mas a política comparava apenas o texto literal 0. | Normalização invariante e validação de tipo/domínio na leitura, gravação e geração. Zero com sinal, zeros adicionais ou espaços recebe a mesma decisão. |
| XML escrevia SshClient e lia apenas SshPreference; XML/CSV omitiam agente e parâmetros específicos. | XML aceita os nomes atual e legado. XML e CSV próprios preservam SshClient, SshUseAgent e VncParameters; o mapeamento CSV da interface repassa esses campos. |
| Ações, mensagens e rótulos novos permaneciam em português. | Catálogos PT/EN/ES completos para importação, cópia, status, Gateway, diagnóstico RDP, restauração da fonte e erros recentes. A cópia segue o idioma da interface e não traduz valores do usuário. |
| Entregas distintas continuavam identificadas como 1.5.0 e a documentação apontava esquema/distribuições antigos. | Assembly/arquivo 1.6.0.0, versão de startup derivada do assembly, esquema atual 10 documentado, publicação reproduzível com manifesto SHA-256. |

A inspeção visual desta entrega também encontrou a cor branca da aba selecionada sendo herdada pelos rótulos da aba Geral no tema claro. O conteúdo rolável de configurações agora usa PrimaryTextBrush, sem alterar o desenho dos painéis. A regressão WPF verifica o rótulo de tamanho da fonte nos dois temas.

## Contratos preservados

- Só Salvar/Ctrl+S persiste cadastros. Navegar, conectar, fechar e criar rascunhos não salvam nem solicitam confirmação.
- IDs estáveis, rascunhos por sessão, senhas pendentes em memória, intenção de manter/substituir/remover, credenciais compartilhadas, rollback e recuperação do cofre permanecem.
- Painéis lado a lado, busca incremental com acentos, Search, filtros, seleção múltipla, status Ativo/Inativo, importação RDP/Gateway e botões individuais de cópia permanecem.
- Esquema SQLite continua 10: nenhuma migração nova ou edição das bases pessoais foi necessária.
- XML/CSV próprios não transportam senha nem vínculo local de credencial. Para transporte de credenciais, permanece o pacote criptografado. Ausência de campos SSH em arquivos antigos usa Default, agente habilitado e parâmetros VNC vazios. AutoPuTTY mantém seu formato compatível, com as limitações próprias desse formato.

Os domínios RDP preservam a política anterior contra desativar CredSSP/negociação e autenticação de nível zero. Os modos documentados de origem de credencial do gateway 0–5 são preservados. Referência primária consultada para os valores: [Microsoft — RDP properties](https://learn.microsoft.com/en-us/previous-versions/windows/apps/hh994953(v=win.10)). Não foi introduzida propriedade de título no arquivo RDP.

## Arquivos alterados

- `src/ConectaTI.App/ViewModels/MainViewModel.cs` e `MainViewModel.CopyAll.cs`: validade das operações de senha e status.
- `src/ConectaTI.App/Views/CredentialsDialog.xaml.cs`: isolamento de revelação/salvamento, snapshots e conclusões atrasadas.
- `src/ConectaTI.App/Security/ClipboardService.cs`: validade por tentativa; falha/cancelamento não cancela a limpeza da cópia anterior.
- `src/ConectaTI.App/Security/CredentialVaultService.Storage.cs`: pontos virtuais estreitos para ensaiar atrasos sem mudar o armazenamento real.
- `src/ConectaTI.App/Services/RdpFileService.cs` e `ConnectionPersistenceService.cs`: validação numérica e validação antes do commit.
- `src/ConectaTI.App/Services/ImportExport/ImportExportService.cs` e `Views/ImportExportDialog.xaml.cs`: fidelidade e mapeamento SSH/VNC.
- `src/ConectaTI.App/Services/ConnectionCopyFormatter.cs` e `RdpSessionFile.cs`: rótulos, descrições e mensagens localizadas.
- `src/ConectaTI.App/MainWindow.xaml` e `MainWindow.xaml.cs`: recursos de localização nas ações e diagnóstico recentes.
- `src/ConectaTI.App/Views/ExecutablesSettingsDialog.xaml`: tradução da fonte e cor do conteúdo das abas.
- `src/ConectaTI.App/Localization/Strings.resx`, `Strings.en.resx` e `Strings.es.resx`: 759 entradas por catálogo.
- `src/ConectaTI.App/ConectaTI.App.csproj` e `App.xaml.cs`: versão 1.6.0 e identificação derivada do assembly.
- `tests/ConectaTI.Tests/CredentialSelectionRaceTests.cs`, `Delivery160Tests.cs` e `Localization160Tests.cs`: 35 novos casos.
- `tests/ConectaTI.Tests/StaTest.cs` e `UnitTest1.cs`: Application WPF compartilhada no STA/Dispatcher ativo e contraste das preferências nos dois temas.
- `tools/ConectaTI.UiSmoke/Program.cs`: seleção isolada de idioma/pasta para a conferência desta entrega.
- `tools/Publish-Release.ps1`: build, suíte, publicação nova e conferência do conteúdo/versão/hash.
- `README.md`, `CONTEXTO.md` e este relatório: comportamento, compatibilidade, versão e evidências atuais.

## Verificações efetivamente realizadas

Windows, SDK .NET 10.0.403. Todos os cadastros/segredos de teste são fictícios e ficam em pastas temporárias ou pastas isoladas do iniciador de testes; não houve teste com banco pessoal.

1. Compilação Release da solução, sem avisos nem erros.
2. Suíte completa: 257 executados, 257 aprovados, zero falhas e zero ignorados. Evidência final: `artifacts/test-results/release-1.6.0.trx`. Inclui as regressões anteriores de rascunhos, salvamento com navegação/edição, remoção de senha, compartilhamento, persistência, rollback, migração e transferência.
3. Corridas com cofre controlado e resposta liberada após a troca A→B: cópia individual, completa e revelação, tanto sucesso quanto falha. Nenhuma senha/status antigo aplicado a B. Também exercitados edição posterior, fechamento, A→B→A e retry do clipboard.
4. Diálogo CredentialsDialog real no Windows/STA: seleção alterada durante revelação; resposta e falha antigas descartadas. Salvamento atrasado de A não recoloca seleção nem apaga a edição de B. Leitura/gravação do cofre são substituídas apenas nesses testes para controlar a ordem dos awaits.
5. RDP: variantes 00, +0, espaço antes/depois e -0; tipo s/b incorreto, faixas inválidas, serialização canônica e modos de gateway válidos. Importação, persistência e geração exercitadas.
6. Ida e volta campo a campo do cadastro editável em CSV, XML, JSON e pacote próprio, para cada protocolo, incluindo SSH/agente/chave, VNC, RDP/Gateway, VPN, status e favorito. Alias XML legado e ausência de campos antigos exercitados.
7. Catálogos: mesmas chaves e parâmetros nos três idiomas; textos recentes, rótulos da cópia, erros e valores técnicos invariáveis exercitados em PT/EN/ES.
8. UiSmoke de navegação: busca, foco, setas, pausa/timer, seleção distante virtualizada, limites, visualização filtrada, Search, seleção estendida e foco no editor. Execução nova com janela estreita, fonte 18 e tema escuro; marcador `tools/ConectaTI.UiSmoke/bin/Release/net10.0-windows/navigation-checks.txt`.
9. Inspeção visual da janela WPF isolada: 620 × 760, fonte 18, tema escuro, inglês; cópia completa e opções Gateway/diagnóstico expandidas, textos auxiliares e checkbox com quebra de linha, rolagem e painéis lado a lado.
10. Executável publicado iniciado com `--data-dir` em pasta temporária contendo somente fixture fictícia; espanhol, fonte 11 e tema claro. Ações recentes traduzidas e layout lado a lado conferidos; startup identificou 1.6.0.0. Configurações de fonte também conferidas visualmente no binário final: rótulos legíveis após a correção de cor e botão de restauração alterando 11 para 12,5. A preferência foi exercitada somente na pasta temporária fictícia.

As primeiras execuções foram usadas para corrigir o ciclo de vida do host WPF e rótulos faltantes. Os totais acima correspondem à execução final após as correções, não aos relatórios históricos recebidos.

## Publicação e limites

Comando reproduzível: `.\tools\Publish-Release.ps1 -Dotnet "$env:LOCALAPPDATA\ConectaTI-Development\dotnet\dotnet.exe"`. O script recusa pasta existente e não copia arquivos da instalação anterior.

Entrega: `dist/ConectaTI-1.6.0-win-x64/ConectaTI.exe`, Windows x64 autocontido, acompanhado somente de `release-manifest.json`. Versão de arquivo 1.6.0.0. Banco, configurações, logs, backups e credenciais não integram essa pasta. O manifesto registra tamanho e SHA-256 do executável final.

Tamanho: 67026984 bytes. SHA-256: `48A56E3361E046E4ABDB31AF0E7651819A16266FDE489E773AE189C8901DEE38`.

Nenhuma sessão autenticada em MSTSC, SSH, WinSCP ou VNC foi estabelecida nesta entrega. Abrir um cliente não comprova autenticação. Título de sessão RDP autenticada/barra de tarefas, XML original do incidente, DPI 125%/150% e múltiplos monitores continuam dependentes do ambiente de uso.

O Git local já retorna `error: bad tree object HEAD`. Não foi reparado, resetado nem usado para sobrescrever fontes/dados. A entrega está nos arquivos da pasta e no executável; não houve commit ou push.

A refatoração estrutural e a padronização visual ampla permanecem para a próxima fase, conforme solicitado.
