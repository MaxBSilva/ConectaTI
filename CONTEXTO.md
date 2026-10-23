# Contexto do ConectaTI

Projeto desenvolvido e mantido por **Max Silva** ([MaxBSilva](https://github.com/MaxBSilva)).

Referência funcional e técnica do projeto, revisada em 23/10/2026 para a versão 1.2.1. Este documento descreve o código atual; decisões históricas, correções e evidências de testes estão em [RELATORIO-CORRECOES-1.2.1.md](RELATORIO-CORRECOES-1.2.1.md).

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
| Enter / duplo clique na lista | Conecta ao acesso selecionado |
| Esc na busca | Limpa a busca |
| Esc na lista | Limpa a busca incremental |

Não assumir atalhos ou bloqueio de aplicativo descritos em documentação antiga: a janela principal atual não implementa Ctrl+L e `IsLocked` retorna `false`.

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

`CredentialVaultService` usa DPAPI do usuário Windows e integração com o Gerenciador de Credenciais do Windows. Copiar uma pasta com o SQLite para outro usuário/computador não garante que suas senhas possam ser decifradas. Para transporte de credenciais existe o pacote criptografado `.ctienc`.

Há compatibilidade com credenciais legadas protegidas por senha mestra e fluxo de migração. Isso não representa um bloqueio ativo da aplicação na versão atual.

### Consistência da gravação

`ConnectionPersistenceService` coordena duas persistências que não compartilham transação:

1. Em substituições, prepara uma credencial com novo ID, mantendo a anterior intacta.
2. Registra uma instrução durável de limpeza e prepara a cópia no cofre Windows.
3. Grava a nova credencial e o vínculo da conexão na mesma transação SQLite.
4. Retira a credencial anterior apenas se nenhuma conexão, inclusive na lixeira, ainda a referencia.
5. Executa a limpeza do cofre depois do commit. Falhas deixam uma tarefa em `PendingVaultCleanup`, recuperada posteriormente.

Uma substituição no formulário cria uma credencial dedicada, sem alterar silenciosamente os outros acessos que compartilham a anterior. Nunca assumir que o rollback SQLite desfaz uma operação no cofre Windows.

Senhas não devem ser registradas em logs, relatórios ou argumentos de processo. A integração RDP usa mecanismos próprios do Windows; SSH, WinSCP e VNC não recebem senhas por parâmetros inseguros de linha de comando.

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

`DatabaseMigrationManager` trata descoberta e migração de bases legadas. `SqliteRepository.InitializeAndMigrate` aplica migrações de esquema e faz backup consistente antes de atualizar uma base existente. A versão 1.2.1 usa esquema 5, que inclui `PendingVaultCleanup`.

Não editar a base do usuário em testes nem sobrescrevê-la ao publicar. SQLite não deve ser usado como banco de equipe com múltiplas instâncias gravando em um compartilhamento de rede.

## Importação, exportação e lixeira

- `ImportExportService` reúne importações/exportações e tratamento de duplicidades.
- `AutoPuttyCodec` trata formatos AutoPuTTY suportados e o formato XML de intercâmbio. Arquivos proprietários criptografados exigem uma exportação compatível na origem.
- CSV e JSON podem transportar cadastros; a exportação padrão não inclui senhas em texto puro.
- O pacote `.ctienc` permite transporte criptografado de conexões e credenciais com senha de transporte.
- Backups e restauração usam mecanismos consistentes do SQLite e verificações de integridade.
- Excluir um acesso normalmente o move à lixeira; exclusão definitiva e esvaziamento são ações distintas com confirmação própria. Essas confirmações não são diálogos de salvamento.

## Arquitetura e mapa do código

A solução é C#/.NET 10 para Windows, WPF com MVVM e SQLite. As dependências de produção declaradas são `CommunityToolkit.Mvvm`, `Microsoft.Data.Sqlite` e `Konscious.Security.Cryptography.Argon2`.

| Local | Responsabilidade |
| --- | --- |
| `src/ConectaTI.App/App.xaml.cs` | Composição dos serviços, migração inicial e abertura da janela |
| `MainWindow.xaml` e `.xaml.cs` | Interface, atalhos, foco, eventos e sincronização protegida dos controles de senha |
| `ViewModels/MainViewModel.cs` | Seleção, filtros, modos de visão, sessões de edição, comandos e estado visual |
| `Models/ConnectionEntry.cs` | Cadastro observável, identidade e comparação dos campos editáveis |
| `Models/ConnectionEditSession.cs` | Cópia de edição, baseline, revisões e intenção de senha |
| `Services/ConnectionPersistenceService.cs` | Coordenação do commit SQLite e operações no cofre |
| `Data/SqliteRepository.cs` | Acesso ao SQLite, transações, backups e migrações de esquema |
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

Use Windows com SDK .NET 10. O executável publicado como self-contained não exige o SDK. No ambiente em que esta versão foi validada, o SDK era 10.0.402; o `dotnet` do PATH continha somente runtime, e foi necessário usar o executável em `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.

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
  -o dist/win-x64-portatil-v1.2.1
```

Distribuir somente os arquivos de publicação previstos. Não incluir SQLite, configurações, logs, backups ou credenciais. Para atualizar uma instalação existente, preservar os dados e substituir o executável após backup.

O `.gitignore` mantém `bin/`, `obj/`, `artifacts/`, `dist/`, arquivos locais de usuário e pacotes fora do histórico Git. Executáveis de distribuição são artefatos separados do código-fonte.

## Validação e limites conhecidos

A entrega 1.2.1 teve compilação Release sem erros/avisos e 111 testes aprovados na última execução registrada em `RELATORIO-CORRECOES-1.2.1.md`. Essa evidência é da entrega registrada, não substitui executar a suíte após mudanças futuras.

As regressões essenciais são seleção ↔ formulário, lista após salvar, filtros/abas, rascunhos A/B, novas identidades, edições durante save, reverter/descartar, senha protegida/revelada/removida, compartilhamento de credenciais, rollback e falhas do cofre.

Continuam dependendo do ambiente do usuário: autenticação real nos servidores, clientes externos instalados, permissões locais, DPI/múltiplos monitores e a sequência exata do vídeo que não foi anexado. A presença de testes de launcher não comprova uma sessão remota autenticada.

## Direção para próximas alterações

Manter o aplicativo leve e portátil. Priorizar correções pontuais e preservar AutoPuTTY e os dados existentes. Mudanças em seleção, edição ou credenciais devem manter os contratos descritos neste documento e acrescentar regressões quando houver um defeito novo.

Servidor central, permissões de equipe e sincronização são evoluções futuras, não funcionalidades implementadas nesta versão. Caso sejam solicitadas, precisam de projeto e migração próprios, sem transformar o SQLite portátil em um banco compartilhado improvisado.
