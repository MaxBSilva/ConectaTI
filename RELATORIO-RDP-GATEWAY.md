# RD Gateway e importação RDP — 29/10/2026

## Diagnóstico

O launcher já gerava um .rdp e chamava mstsc.exe com argumentos separados. Porém, modelo, persistência, interface e gerador não possuíam suporte a RD Gateway. Não eram transmitidos gatewayhostname, gatewayusagemethod, gatewaycredentialssource, gatewayprofileusagemethod ou promptcredentialonce. Redirecionamentos e outras propriedades do arquivo funcional também não eram preservados.

A omissão foi confirmada no código. Isso sustenta a hipótese de configuração incompleta, mas não prova a causa da falha real na Insolo. Nenhuma conexão com esse ambiente foi tentada.

## Recuperação dos fontes

Arquivos de código/documentação ausentes foram recuperados das partes locais 001 e 003 do mesmo ZIP, sem sobrescrever arquivos presentes nem copiar bancos, segredos ou configurações pessoais. O Git local já possuía objetos ausentes (bad tree object HEAD); não houve commit ou push. O remoto foi consultado somente para verificar disponibilidade.

O SDK .NET 10.0.403 foi instalado isoladamente em %LOCALAPPDATA%/ConectaTI-Development/dotnet, pois dotnet não estava disponível no PATH.

## Arquivos implementados/alterados

- src/ConectaTI.App/Models/RdpOptions.cs: opções observáveis de gateway e propriedades RDP adicionais.
- src/ConectaTI.App/Models/ConnectionEntry.cs: eventos, clone profundo e comparação de alterações.
- src/ConectaTI.App/Services/RdpFileService.cs: parser, importação, sanitização, precedência e geração centralizada.
- src/ConectaTI.App/Data/SqliteRepository.cs: migração 9 e persistência por conexão.
- src/ConectaTI.App/Services/ConnectionPersistenceService.cs: sanitização antes do commit.
- src/ConectaTI.App/Services/Launchers/ConnectionLauncherService.cs: geração completa, temporários exclusivos e prévia segura.
- src/ConectaTI.App/Services/AppPaths.cs: limpeza conservadora de temporários.
- src/ConectaTI.App/Services/ImportExport/ImportExportService.cs: novas opções nos formatos próprios.
- src/ConectaTI.App/Helpers/ConnectionValidator.cs: validação das propriedades e de segurança.
- src/ConectaTI.App/ViewModels/MainViewModel.Rdp.cs: rascunho importado e diagnóstico.
- src/ConectaTI.App/MainWindow.xaml e MainWindow.xaml.cs: importação, gateway recolhível e área avançada.
- tests/ConectaTI.Tests/RdpTests.cs: 21 casos novos.
- tests/ConectaTI.Tests/ApplicationVaultTests.cs: expectativa do esquema atualizada.
- README.md e CONTEXTO.md: uso e contratos técnicos.

## Como usar e validar o caso real

1. Abra dist/ConectaTI-RDP-Gateway/ConectaTI.exe. A distribuição Windows x64 inclui o runtime e não inclui dados pessoais.
2. Clique em **Importar RDP**, selecione seu arquivo funcional e revise o novo rascunho. O anexo desta conversa era o prompt, não o .rdp completo; os testes reproduzem os parâmetros fornecidos com servidor e conta fictícios.
3. Confira o aviso de separação de /admin. O endereço deve ficar INSOLOLV3APP02; Admin (/admin) deve estar habilitado. Revise nome Insolo — APP02 e usuário INSOLOBR\soel.max.
4. Em **Mais opções → RD Gateway**, confira sistemas.insolo.com.br, utilização **2**, origem **4**, perfil **1** e mesmas credenciais habilitado.
5. Em **RDP avançado e diagnóstico**, confira as demais propriedades importadas. Visualize/exporte a configuração sem segredos para comparação. A prévia usa o formulário atual; Salvar persiste as alterações.
6. Clique em **Salvar**, feche/reabra e confira os valores. A importação cria um ID novo e não substitui outro acesso.
7. Clique em **Conectar** e responda às solicitações de credenciais do MSTSC. Confirme que o desktop remoto abriu: processo iniciado não comprova autenticação.

Configuração manual: os mesmos campos ficam em **Mais opções → RD Gateway** para MSTSC. Não é necessário salvar senha. Para preservar todos os redirecionamentos, importe o arquivo ou informe as propriedades na área avançada.

## Contratos e diferenças intencionais

Os números de gateway 2/4/1 são preservados. Campos estruturados prevalecem sobre propriedades equivalentes da área avançada. Não há duplicatas na saída; entradas repetidas usam a última ocorrência. Valores com dois-pontos e caminhos com espaços são mantidos.

/admin sai do endereço e vira argumento separado (também administrative session=1). DOMINIO\usuario pode virar username + domain. Server port e enablecredsspsupport=1 são explícitos; prompt for credentials on client recebe o padrão do app quando ausente. Ordem das linhas não define equivalência.

Senhas, propriedades contendo password/secret/token, binários e assinatura são excluídos da importação e diagnóstico. A cópia original mantém as demais linhas e sua ordem. A saída nunca é apresentada como assinada. Configurações que desabilitam CredSSP, negociação de segurança ou aceitam certificado sem aviso são rejeitadas.

A migração 9 acrescenta RdpOptionsJson com default {}, transação e backup prévio de bases existentes. JSON, pacotes, CSV e XML próprios preservam o grupo de opções; CSV/XML também preservam tela/monitores/admin. Backups SQLite abrangem a nova coluna. AutoPuTTY mantém as limitações do formato externo.

O transporte de senhas existente DPAPI password 51 e TERMSRV permanece, sem senha em texto puro. Cada abertura tem arquivo temporário aleatório; limpeza só ocorre após um dia e sem processo MSTSC. Credenciais separadas de gateway não são armazenadas; o MSTSC solicita autenticação conforme origem/perfil importados.

## Verificações executadas neste Windows

- Build Release: sucesso, zero avisos/erros.
- Suíte Release: **194 aprovados, 0 falhas, 0 ignorados**.
- Relatório: tests/ConectaTI.Tests/TestResults/rdp-final.trx.
- Os 21 casos novos cobrem equivalência semântica dos parâmetros, UTF-8 com/sem BOM, UTF-16 LE/BE com BOM, dois-pontos, caminhos com espaços, /admin restrito, rejeição de comandos, persistência/reabertura, migração antiga, precedência/duplicatas, clone/estado de edição, salvamento explícito, transporte protegido, ausência de senha em diagnóstico/logs, CSV/XML/JSON e rejeição de redução de segurança.
- Conferência visual pelo ConectaTI.UiSmoke com banco fictício isolado: lista/formulário lado a lado, importação na barra, expansão/recolhimento de gateway e área avançada.
- Publicação self-contained Windows x64: dist/ConectaTI-RDP-Gateway.

O teste do launcher usa processo substituído, não uma sessão autenticada na Insolo.

## Referências consultadas

- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings
- https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings-gatewayusagemethod
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings-gatewaycredssource
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings-gatewayprofileusagemethod
