namespace ConectaTI.App.Services;

public sealed record KeyboardShortcutItem(
    string Shortcut,
    string Action,
    string Context,
    string Description);

public static class KeyboardShortcutsCatalog
{
    public static IReadOnlyList<KeyboardShortcutItem> GetAll() =>
    [
        new("Ctrl+N", L.Text("Nova conexão"), L.Text("Janela principal"), L.Text("Cria um novo rascunho em memória e foca o campo Nome.")),
        new("Ctrl+S", L.Text("Salvar alterações"), L.Text("Janela principal"), L.Text("Persiste explicitamente os dados e a intenção de senha do acesso atual.")),
        new("Ctrl+F", L.Text("Buscar conexão"), L.Text("Janela principal"), L.Text("Foca o campo Buscar e seleciona o filtro atual.")),
        new("F2", L.Text("Focar nome da conexão"), L.Text("Lista ou formulário"), L.Text("Move o foco diretamente para o campo Nome do acesso em edição.")),
        new("Ctrl+,", L.Text("Abrir Configurações"), L.Text("Janela principal"), L.Text("Abre a janela de Configurações do aplicativo.")),
        new("F1", L.Text("Consultar atalhos"), L.Text("Janela principal"), L.Text("Abre diretamente a aba Atalhos nas Configurações.")),
        new("Enter", L.Text("Conectar"), L.Text("Lista de acessos"), L.Text("Inicia a conexão selecionada (não dispara durante a edição de campos).")),
        new("↑ / ↓", L.Text("Navegar pelos acessos"), L.Text("Lista de acessos"), L.Text("Move um item visível por vez mantendo seleção, foco e rolagem sincronizados.")),
        new("A–Z / 0–9", L.Text("Busca incremental"), L.Text("Lista de acessos"), L.Text("Localiza rapidamente pelo nome ao digitar com a lista em foco.")),
        new("Esc", L.Text("Limpar busca / Fechar"), L.Text("Busca, lista ou diálogos"), L.Text("Limpa o filtro quando a caixa de busca está em foco, encerra a busca incremental ou fecha diálogos sem apagar rascunhos.")),
        new("Tab / Shift+Tab", L.Text("Navegar entre controles"), L.Text("Todas as janelas"), L.Text("Percorre comandos, filtros, lista e campos em ordem lógica com foco visível."))
    ];
}
