# Ajustes de interface e cópia — 29/10/2026

## Resultado

Restaurado o botão **Copiar todos os dados**, abaixo de Conectar/Salvar/menu e ocupando a largura do painel. Usa os valores atuais do formulário, inclusive alterações não salvas, sem depender da expansão das seções. Mantidos os botões individuais.

A senha só é recuperada no comando explícito. São respeitadas as intenções de substituir/manter/remover; uma remoção pendente não copia a senha antiga. A leitura assíncrona captura sessão, revisão e identidade e abandona a cópia se a seleção mudar durante a recuperação. Nenhum segredo é enviado aos logs/status. A confirmação é apenas “Dados do acesso copiados”. O conteúdo usa o serviço de área de transferência existente, com limpeza temporizada somente se ainda corresponder à cópia original.

Incluídos nome, tipo, endereço/porta, usuário, senha disponível, cliente, grupo, ambiente informado, observações, opções RDP aplicáveis, Gateway e VPN. Campos vazios e ambiente não informado são omitidos. Booleanos relevantes são Sim/Não; zero válido é mantido. A resolução é omitida em tela inteira/multimon e opções RDP são omitidas para outros protocolos. Gateway desativado (modos 0/4) é omitido. Os modos são descritos sem modificar os números. Não há credenciais próprias de gateway armazenadas nesta implementação; permanece a solicitação pelo MSTSC.

O cabeçalho perdeu o bloco interno duplicado de marca. Todas as ações foram mantidas em uma faixa horizontal compacta. Quando a fonte máxima ultrapassa a largura disponível, há rolagem horizontal, sem empilhar os painéis ou tornar ações inacessíveis.

Gateway e diagnóstico usam os recursos dinâmicos do tema: títulos claros, explicações secundárias, entradas e seleção padronizadas. Checkboxes permitem quebra do texto; o estado desabilitado dos botões mantém contraste. A janela de diagnóstico acompanha tema e fonte.

O ajuste de fonte já tinha infraestrutura no projeto. Foi preservada a faixa **11 a 18**, adicionados indicador e **Restaurar tamanho padrão (12,5)** em **Configurações → Geral**. A mudança é imediata e salva em settings.json. As áreas multilinha recalculam suas alturas nos dois sentidos; campos, botões e linhas usam medida natural, recursos dinâmicos e rolagem existentes. Não há migração de dados nova.

## Arquivos alterados

- src/ConectaTI.App/Services/ConnectionCopyFormatter.cs (novo)
- src/ConectaTI.App/ViewModels/MainViewModel.CopyAll.cs (novo)
- src/ConectaTI.App/MainWindow.xaml
- src/ConectaTI.App/MainWindow.xaml.cs
- src/ConectaTI.App/App.xaml
- src/ConectaTI.App/Services/ViewPreferencesService.cs
- src/ConectaTI.App/Views/ExecutablesSettingsDialog.xaml
- src/ConectaTI.App/Views/ExecutablesSettingsDialog.xaml.cs
- tests/ConectaTI.Tests/CopyAllTests.cs (novo)
- tools/ConectaTI.UiSmoke/Program.cs (opções de fonte/tema/largura para teste isolado)
- README.md, CONTEXTO.md e este relatório

## Verificações

- Build Release: sucesso, zero avisos/erros.
- Suíte: **202 aprovados, zero falhas**, incluindo os testes anteriores de importação RDP, geração, migração/persistência, gateway, credenciais e exportação.
- Oito casos adicionais: cópia do rascunho e senha pendente, troca de seleção sem dados anteriores, Gateway com origem/perfil zero, VPN, senha salva e remoção pendente, omissão de recursos desabilitados, ausência de senha nos logs/diagnóstico, persistência das fontes 11/12,5/18 e retorno ao padrão.
- Evidência automatizada: tests/ConectaTI.Tests/TestResults/interface-copy-final.trx.
- Inspeção visual com a skill computer-use e ConectaTI.UiSmoke, banco fictício: janela estreita com painéis lado a lado, tema escuro, fonte mínima/máxima/padrão, crescimento/redução em tempo real nas configurações, restauração para 12,5, cabeçalho sem marca duplicada, Gateway expandido, rótulos quebrando linha e cópia com seções recolhidas mostrando apenas a confirmação discreta.
- Executável Windows x64 com runtime em dist/ConectaTI-Interface-RDP/ConectaTI.exe, em pasta separada dos dados pessoais.

## Limitações

O histórico Git local não pôde ser percorrido por objetos ausentes. A restauração foi implementada sobre os fontes atuais e o serviço seguro existente. Não houve commit/push nem alteração dos cadastros pessoais. A inspeção visual usou dados fictícios e não cobre toda combinação possível de DPI/tema/tamanho de janela. A conexão real com o cliente não foi testada; abrir MSTSC não comprova autenticação.
