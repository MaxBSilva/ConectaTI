using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services.ImportExport;

public sealed class ExportSelectionItem(ConnectionEntry entry) : ObservableObject
{
    public ConnectionEntry Entry {get;}=entry.Clone();
    private bool _isSelected;
    public bool IsSelected {get=>_isSelected;set=>SetProperty(ref _isSelected,value);}
}
public sealed class ExportSelectionModel : ObservableObject, ILanguageObserver
{
    public ExportSelectionModel()=>LocalizationService.Current.Observe(this);
    public void OnLanguageChanged(){foreach(var item in Items)item.Entry.RefreshDisplayProperties();Refresh();}
    public ObservableCollection<ExportSelectionItem> Items {get;}=new();
    private bool _updating;
    private string _query="",_client="",_group="",_name="",_environment="";
    private bool _review,_exportAll=true;
    public string Query {get=>_query;set{if(SetProperty(ref _query,value))Refresh();}}
    public string Client {get=>_client;set{if(SetProperty(ref _client,value))Refresh();}}
    public string Group {get=>_group;set{if(SetProperty(ref _group,value))Refresh();}}
    public string Name {get=>_name;set{if(SetProperty(ref _name,value))Refresh();}}
    public string Environment {get=>_environment;set{if(SetProperty(ref _environment,value))Refresh();}}
    public bool ReviewOnly {get=>_review;set{if(SetProperty(ref _review,value))Refresh();}}
    public bool ExportAll
    {
        get=>_exportAll;
        set
        {
            if(!SetProperty(ref _exportAll,value))return;
            if(value) { _updating=true;try{foreach(var item in Items)item.IsSelected=true;}finally{_updating=false;} }
            Refresh();
        }
    }
    private static bool Contains(string? text,string filter)=>string.IsNullOrWhiteSpace(filter) ||
        (text??"").Contains(filter.Trim(),StringComparison.CurrentCultureIgnoreCase);
    public IEnumerable<ExportSelectionItem> Filtered => Items.Where(i=>
        (!_review || i.IsSelected) && Contains(i.Entry.ClientName,_client) && Contains(i.Entry.GroupName,_group) &&
        Contains(i.Entry.Name,_name) && Contains(i.Entry.EnvironmentDisplay,_environment) &&
        Contains(string.Join(" ",i.Entry.Name,i.Entry.ClientName,i.Entry.GroupName,i.Entry.EnvironmentDisplay),_query));
    public string CountText=>L.Format("{0} acesso(s) selecionado(s) de {1}. Resultados filtrados: {2}.",Items.Count(i=>i.IsSelected),Items.Count,Filtered.Count());
    public string[] SelectedIds=>Items.Where(i=>i.IsSelected).Select(i=>i.Entry.Id).ToArray();
    public void Reload(IEnumerable<ConnectionEntry> entries)
    {
        var selected=SelectedIds.ToHashSet();
        _updating=true;
        try
        {
            foreach(var old in Items)old.PropertyChanged-=SelectionChanged;
            Items.Clear();
            foreach(var entry in entries.Where(c=>!c.IsDeleted))
            {
                var item=new ExportSelectionItem(entry) {IsSelected=_exportAll || selected.Contains(entry.Id)};
                item.PropertyChanged+=SelectionChanged;Items.Add(item);
            }
        }
        finally{_updating=false;}
        Refresh();
    }
    private void SelectionChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_updating)return;
        if(_exportAll){_exportAll=false;OnPropertyChanged(nameof(ExportAll));}
        Refresh();
    }
    public void SelectFiltered()
    {
        var filtered=Filtered.ToList();
        _updating=true;try{foreach(var item in filtered)item.IsSelected=true;}finally{_updating=false;}
        _exportAll=false;OnPropertyChanged(nameof(ExportAll));Refresh();
    }
    public void Clear()
    {
        _updating=true;try{foreach(var item in Items)item.IsSelected=false;}finally{_updating=false;}
        _exportAll=false;OnPropertyChanged(nameof(ExportAll));Refresh();
    }
    public void Refresh(){OnPropertyChanged(nameof(Filtered));OnPropertyChanged(nameof(CountText));}
}
