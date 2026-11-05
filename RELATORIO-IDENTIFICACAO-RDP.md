# Identificação das janelas RDP — 30/10/2026

## Investigação e implementação

O ConectaTI abria mstsc.exe com um arquivo `conectati-GUID.rdp`, usando ProcessStartInfo.ArgumentList e UseShellExecute=false. O GUID no nome do arquivo explicava o identificador relatado na janela de sessão. Não foi acrescentada nenhuma propriedade RDP de título, nem tentativa de renomear janelas por PID/endereço.

O arquivo agora recebe o nome legível **Nome cadastrado - Endereço꞉porta.rdp**. A exclusividade fica na pasta-pai `temp/conectati-GUID/`, diferente em cada abertura. Assim, acessos diferentes ao mesmo destino recebem nomes próprios, e aberturas repetidas não sobrescrevem arquivos. O nome não contém usuário, credencial ou ID do cadastro.

O endereço é normalizado com HostPortParser: a porta explícita no host não é duplicada e IPv6 mantém os colchetes. A porta configurada é incluída mesmo quando é 3389. O nome é limitado ao orçamento de caminho do cliente, preservando espaço para o endereço. Acentos são mantidos; caracteres proibidos, controles e marcas direcionais são tratados. Como `:` não pode integrar um nome de arquivo Windows, ele é representado por **꞉ (U+A789)** na identificação visual. No conteúdo RDP, endereço e porta continuam com sua sintaxe original, incluindo `:` ASCII.

O sufixo “Conexão de Área de Trabalho Remota” pertence ao MSTSC e ao idioma instalado. Ele não é repetido no nome do arquivo. A limpeza conservadora também trata as novas subpastas, sem apagar recursivamente arquivos inesperados e sem limpar enquanto houver MSTSC em execução. O fluxo de credenciais e as propriedades RDP/Gateway permanecem inalterados.

## Arquivos alterados

- src/ConectaTI.App/Services/RdpSessionFile.cs: nomes legíveis, endpoint e isolamento por abertura.
- src/ConectaTI.App/Services/Launchers/ConnectionLauncherService.cs: utiliza o novo caminho e cria sua pasta antes de gravar.
- src/ConectaTI.App/Services/AppPaths.cs: limpeza das novas pastas temporárias.
- tests/ConectaTI.Tests/RdpSessionFileTests.cs: segurança dos nomes, acentos, portas, IPv6, limites e 100 aberturas paralelas sem colisão.
- tests/ConectaTI.Tests/RdpTests.cs: compara geração de dois acessos com nomes distintos, mesmo destino, conteúdo RDP idêntico e /admin preservado.
- tools/ConectaTI.RdpTitleSmoke/Program.cs e ConectaTI.RdpTitleSmoke.csproj: inspeção isolada com nomes fictícios e destino somente loopback.
- README.md, CONTEXTO.md e este relatório.

## Verificações e limites reais

- Build Release: sucesso, zero avisos/erros.
- Testes Release: **213 aprovados, zero falhas**. Evidência: tests/ConectaTI.Tests/TestResults/rdp-titles-final.trx.
- Dois arquivos de nomes distintos foram abertos simultaneamente no MSTSC real, versão **10.0.26100.9949**, com o mesmo destino fictício `127.0.0.1:65534`. Os nomes gerados foram “ERP Produção fictício - 127.0.0.1꞉65534.rdp” e “HCM fictício - 127.0.0.1꞉65534.rdp”, em diretórios separados.
- A interface foi inspecionada pela skill computer-use. No modo `/edit`, o cliente exibiu o endereço correto e manteve o título genérico **Conexão de Área de Trabalho Remota**, comportamento próprio da janela de configuração.
- Foram iniciadas também tentativas normais contra loopback, sem credenciais. Não havia serviço RDP local ativo (TermService parado), portanto nenhuma sessão autenticada foi estabelecida.
- **Não foi possível validar o título final de uma sessão conectada nem a identificação correspondente na barra de tarefas/miniatura.** A janela de edição não comprova esse resultado. A barra de tarefas pode agrupar sessões sob o ícone do MSTSC; nomes individuais dependem das janelas que o cliente expõe. Não foi afirmado que a conexão real ou a identificação em sessão autenticada tenham sido validadas.

## Validação complementar necessária

Abra, pelo ConectaTI atualizado, dois acessos autorizados com nomes distintos e mesmo destino. Após autenticar, confira o título de cada sessão e as miniaturas do grupo MSTSC na barra de tarefas; feche/reabra um deles para conferir que a outra sessão conserva a identificação. Confira também uma porta personalizada e um nome com acentos. Em tela inteira, a barra de conexão é controlada pelo cliente e pode apresentar o destino em vez do título completo da janela.

Executável Windows x64 com runtime: dist/ConectaTI-Identificacao-RDP/ConectaTI.exe. A distribuição não inclui banco ou credenciais pessoais.

## Referências

- [MSTSC: arquivo de conexão, /edit e argumentos suportados](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc)
- [Propriedades RDP documentadas](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties)

As referências documentam o uso do arquivo de conexão; não oferecem um parâmetro arbitrário de título. A implementação evita inventar essa propriedade.
