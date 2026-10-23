# Guia de desenvolvimento

**ConectaTI — Max Silva**

O [contexto do projeto](CONTEXTO.md) descreve o produto e a arquitetura. O [README](README.md) contém os comandos de uso, compilação e publicação; as [notas da versão 1.2.1](RELATORIO-CORRECOES-1.2.1.md) registram correções e evidências de validação.

## Organização

O projeto utiliza C#/.NET 10 para Windows, WPF com MVVM e SQLite portátil. Preserve a estrutura existente, os recursos de importação AutoPuTTY e a compatibilidade das bases. Prefira mudanças pontuais e evite dependências desnecessárias.

## Contratos de edição

- Use o ID estável da conexão para seleção, edição e gravação.
- Rascunhos e senhas pendentes ficam somente em memória, associados à sessão correta.
- Os campos do cadastro são persistidos por Salvar ou Ctrl+S. Navegar, conectar, abrir novo cadastro e fechar não salvam o formulário nem pedem confirmação de alterações pendentes.
- Antes dos `await` de gravação, capture identidade, valores e revisões. A conclusão deve preservar a seleção e as edições feitas posteriormente.
- Atualize coleções e propriedades vinculadas no Dispatcher WPF. Não bloqueie tarefas assíncronas no fluxo da interface.
- Restaure o estado anterior das proteções de eventos em `finally`, inclusive em chamadas aninhadas.

## Credenciais e dados

- Um campo de senha vazio por proteção mantém a credencial existente. Substituição e remoção são intenções explícitas, aplicadas no commit.
- Preserve credenciais compartilhadas e a recuperação das operações do cofre. SQLite e cofre Windows não compartilham uma transação.
- Não retirar definitivamente a credencial anterior antes da gravação do vínculo correspondente.
- Não registrar senhas nem usar dados pessoais em testes. Utilize pastas temporárias e exemplos fictícios.
- Preserve `sqlite.db`, configurações, backups e migrações. Publique em pasta separada e mantenha arquivos de usuário fora do Git e da distribuição.

## Validação

```powershell
dotnet build ConectaTI.slnx -c Release
dotnet test ConectaTI.slnx -c Release
```

Para testes WPF, use Dispatcher em STA com fluxo assíncrono ativo. O iniciador `tools/ConectaTI.UiSmoke` abre a janela com dados fictícios e evita a descoberta das bases legadas pessoais.

Registre os comandos executados e seus resultados. Evidência histórica não deve ser apresentada como resultado de uma execução nova. Atualize `CONTEXTO.md` e `README.md` quando houver mudanças de comportamento.
