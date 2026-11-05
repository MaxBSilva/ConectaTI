# Orientação para trabalhar no ConectaTI

Leia `CONTEXTO.md` para entender o produto, a arquitetura e os contratos de edição/persistência. O `README.md` contém comandos de uso; `RELATORIO-CORRECOES-1.2.1.md` registra evidências históricas e limites da validação.

- Projeto existente: C#/.NET 10 Windows, WPF/MVVM, SQLite portátil. Preserve recursos e evite dependências ou reestruturações extensas sem necessidade.
- Use o ID estável da conexão. Rascunhos e senhas pendentes ficam apenas em memória e pertencem à sessão de edição correta.
- Campos do cadastro só são persistidos por Salvar/Ctrl+S. Navegação, conexão, novo cadastro e fechamento não devem salvar nem pedir confirmação de alterações pendentes.
- Atualize coleções e propriedades vinculadas no Dispatcher. Não bloqueie operações assíncronas no fluxo da interface. Restaure o estado anterior de proteções de eventos em `finally`.
- Ao salvar, capture identidade, valores e revisões antes dos `await`. Preserve navegação e edições posteriores ao terminar a gravação.
- Senha vazia por proteção mantém a credencial existente; substituição e remoção são intenções explícitas, aplicadas no commit. Preserve credenciais compartilhadas e a recuperação do cofre.
- Banco e cofre Windows não compartilham transação. Não retirar definitivamente a credencial anterior antes de gravar seu vínculo correspondente.
- Não registrar senhas ou usar dados pessoais em testes. Use pastas temporárias e exemplos fictícios. O iniciador `tools/ConectaTI.UiSmoke` evita descoberta das bases legadas pessoais.
- Preserve `sqlite.db`, configurações, backups, migrações e importação AutoPuTTY. Publique em pasta separada e mantenha arquivos de usuário fora do Git e da distribuição.
- Valide mudanças com `dotnet build ConectaTI.slnx -c Release` e testes adequados em `dotnet test ConectaTI.slnx -c Release`. Para WPF, use Dispatcher/STA com fluxo assíncrono ativo. Não declarar testes executados sem evidência.
- Documente mudanças de comportamento em `CONTEXTO.md` e `README.md`. Evidência histórica não deve ser apresentada como resultado de uma execução nova.
