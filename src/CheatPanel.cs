using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    CheatSession? cheatSession;
    Button? cheatButton;
    readonly SemaphoreSlim cheatActions=new(1);
    readonly ComboBox cheatType=new(){ItemsSource=CheatValue.Types.Select(t=>new CheatChoice(t,t=="f32"?"Float (4 bytes)":t=="f64"?"Double (8 bytes)":(CheatValue.Size(t)*8)+"-bit "+(t.StartsWith('u')?"unsigned":"signed"))).ToArray(),DisplayMemberPath="Name",SelectedValuePath="Id",SelectedIndex=2,Width=160}, cheatFilter=new(){ItemsSource=CheatSearch.Filters.Select(f=>new CheatChoice(f,f=="unknown"?"Unknown initial value":char.ToUpperInvariant(f[0])+f[1..].Replace('_',' '))).ToArray(),DisplayMemberPath="Name",SelectedValuePath="Id",SelectedIndex=0,Width=165}, cheatRegion=new(){Width=265,DisplayMemberPath="Name"};
    readonly CheckBox cheatAligned=new(){Content="Aligned addresses only",IsChecked=false};
    readonly TextBox cheatValue=new(){Width=120}, cheatUpper=new(){Width=120}, cheatAddress=new(){Width=125,Text="0x0"}, cheatName=new(){Width=150,Text="Value"}, cheatTarget=new(){Width=140};
    readonly CheckBox cheatLock=new(){Content="Lock selected value"};
    readonly TextBlock cheatContext=new(){Text="No game running",TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Brush(175,202,223),Margin=new Thickness(0,3,0,3)};
    readonly ListView cheatResults=CheatList(("Address","AddressText",100),("Value","Value",90),("Previous","Previous",90),("Region","Region",130));
    readonly ListView cheatWatches=CheatList(("Name","Name",125),("Current","Current",85),("Target","Target",85),("State","State",80));
    readonly TextBlock cheatWatchDetails=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brush(175,202,223),Margin=new Thickness(0,5,0,5)};
    readonly TextBlock cheatMessage=new(){TextWrapping=TextWrapping.Wrap,MaxHeight=48,Foreground=Brush(175,202,223),Margin=new Thickness(0,4,0,4)}, cheatCount=new(){Text="No scan yet",Margin=new Thickness(3),Foreground=Brush(221,231,241)};
    DispatcherTimer? cheatTimer;
    CancellationTokenSource? cheatCancellation;
    bool loadingCheat, cheatBusy;
    int pendingCheatLocks;
    int cheatPage;
    CheatSearch? renderedCheatSearch;
    int renderedCheatScan=-1, renderedCheatPage=-1;
    sealed record RamChoice(int? Index,string Name);
    sealed record CheatChoice(string Id,string Name);
    static ListView CheatList(params (string Label,string Property,double Width)[] columns) {
        var view=new GridView();foreach(var c in columns)view.Columns.Add(new GridViewColumn{Header=c.Label,DisplayMemberBinding=new Binding(c.Property),Width=c.Width});
        var list=new ListView{View=view,Background=Brush(24,32,44),Foreground=Brush(221,231,241),MinHeight=160};
        var headers=new Style(typeof(GridViewColumnHeader));headers.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));list.Resources[typeof(GridViewColumnHeader)]=headers;
        var items=new Style(typeof(ListViewItem));items.Setters.Add(new Setter(Control.ForegroundProperty,Brush(221,231,241)));
        foreach(var property in new[]{ListViewItem.IsSelectedProperty,UIElement.IsMouseOverProperty}){var highlight=new Trigger{Property=property,Value=true};highlight.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));items.Triggers.Add(highlight);}list.ItemContainerStyle=items;return list;
    }
    internal CheatSession Cheats() {
        if(core is not {IsReady:true} || running==null)throw new InvalidOperationException("Launch a DOS program before using cheats.");
        if(cheatSession==null||!ReferenceEquals(cheatSession.Core,core)) {
            cheatSession=new(core,running.Id+"\n"+running.Startup.ToUpperInvariant());cheatPage=0;cheatWatches.ItemsSource=cheatSession.Watches;
        }
        cheatSession.Sync();return cheatSession;
    }
    internal async Task<T> CheatOperation<T>(Func<CheatSession,CancellationToken,Task<T>> action,CancellationToken token) {
        await cheatActions.WaitAsync(token);try {var session=Cheats();var result=await action(session,token);if(!ReferenceEquals(core,session.Core))throw new InvalidOperationException("DOS session changed.");return result;} finally{cheatActions.Release();}
    }
    async Task CheatUi(Func<CheatSession,CancellationToken,Task> action) {
        if(cheatBusy)return;cheatBusy=true;cheatCancellation=new();
        try {await CheatOperation(async (s,t)=>{await action(s,t);return true;},cheatCancellation.Token);await RefreshCheatPanel();}
        catch(OperationCanceledException){cheatMessage.Text="Search cancelled; the previous results are retained.";}
        catch(Exception e){cheatMessage.Text=e.Message;}
        finally{cheatBusy=false;cheatCancellation.Dispose();cheatCancellation=null;}
    }
    void ShowCheats()=>ShowToolTab(1);
    FrameworkElement BuildCheatPanel() {
        var body=new DockPanel{Margin=new Thickness(8)};var intro=new StackPanel();DockPanel.SetDock(intro,Dock.Top);body.Children.Add(intro);
        intro.Children.Add(cheatContext);
        var row=new WrapPanel();row.Children.Add(CheatButton("Pause / resume",()=>core?.Pause(!core.IsPaused)));row.Children.Add(CheatButton("Unlock all",()=>_=CheatUi(async(s,t)=>await s.UnlockAll(t))));row.Children.Add(CheatButton("Refresh",()=>_=RefreshCheatPanel()));intro.Children.Add(row);
        cheatRegion.Width=double.NaN;intro.Children.Add(Label("RAM region",12));intro.Children.Add(cheatRegion);
        cheatMessage.SetBinding(ToolTipProperty,new Binding("Text"){Source=cheatMessage});intro.Children.Add(cheatMessage);
        var compactTabs=new Style(typeof(TabItem),(Style)Resources[typeof(TabItem)]);compactTabs.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(6,5,6,5)));compactTabs.Setters.Add(new Setter(Control.FontSizeProperty,12.0));cheatTabs.Resources[typeof(TabItem)]=compactTabs;
        body.Children.Add(cheatTabs);
        var controls=new StackPanel();controls.Children.Add(CheatFields(("Value type",cheatType),("Scan filter",cheatFilter),("Value",cheatValue),("Upper bound",cheatUpper)));
        controls.Children.Add(cheatAligned);
        row=new WrapPanel();row.Children.Add(CheatButton("First scan",()=>_=ScanCheats(true)));row.Children.Add(CheatButton("Next scan",()=>_=ScanCheats(false)));row.Children.Add(CheatButton("Cancel",()=>cheatCancellation?.Cancel()));row.Children.Add(CheatButton("Clear",()=>_=CheatUi((s,t)=>{s.ResetSearch();cheatPage=0;return Task.CompletedTask;})));var scanActions=row;
        controls.Children.Add(CheatHelp("How to search","Find health, lives or money. First scan → play until the value changes → Next scan. Repeat, then add a result to Watches. Locks are saved OFF and clear after reset/load state."));
        var footer=new StackPanel();footer.Children.Add(scanActions);footer.Children.Add(cheatCount);row=new WrapPanel();row.Children.Add(CheatButton("← Page",()=>{cheatPage=Math.Max(0,cheatPage-1);ShowCheatResults();}));row.Children.Add(CheatButton("Page →",()=>{if(cheatSession!=null&&(cheatPage+1)*200<cheatSession.Search.Count)cheatPage++;ShowCheatResults();}));row.Children.Add(CheatButton("Watch selected",()=>_=AddCheatResult()));footer.Children.Add(row);
        cheatTabs.Items.Add(new TabItem{Header="Search",Content=CheatWorkspace(controls,cheatResults,footer)});
        var edit=new StackPanel();edit.Children.Add(cheatWatchDetails);edit.Children.Add(CheatFields(("Name",cheatName),("Target value",cheatTarget)));edit.Children.Add(cheatLock);
        row=new WrapPanel();row.Children.Add(CheatButton("Apply / write",()=>_=EditCheatWatch()));row.Children.Add(CheatButton("Remove",()=>_=CheatUi(async(s,t)=>{if(cheatWatches.SelectedItem is not CheatWatch w)throw new ArgumentException("Select a watch.");await s.Remove(w.Id,t);cheatWatches.Items.Refresh();})));var watchActions=row;
        var manual=new StackPanel();cheatAddress.Width=double.NaN;manual.Children.Add(cheatAddress);manual.Children.Add(CheatButton("Add address · Search value type",()=>_=CheatUi(async(s,t)=>{var watch=await s.Add(cheatName.Text,CheatValue.Address(cheatAddress.Text),(string)cheatType.SelectedValue,t);cheatWatches.Items.Refresh();cheatWatches.SelectedItem=watch;})));edit.Children.Add(new Expander{Header="Add a mapped address manually",Content=manual,Foreground=Foreground});
        cheatTabs.Items.Add(new TabItem{Header="Watches",ToolTip="Watches & locks",Content=CheatWorkspace(edit,cheatWatches,watchActions)});
        AddAdvancedCheatTabs(cheatTabs);
        cheatTabs.SelectionChanged+=(_,e)=>{if(!ReferenceEquals(e.Source,cheatTabs))return;cheatMessage.Text=cheatTabs.SelectedIndex switch{0=>"First scan → change the value in-game → Next scan.",1=>"Saved per game. Active locks continue while the panel is hidden.",2=>"Rescan saved paths after locating the value at its new address.",_=>"Read-only x86. Set CPU mode and logical IP before decoding."};};
        cheatWatches.SelectionChanged+=CheatWatchSelected;cheatLock.Checked+=CheatLockChanged;cheatLock.Unchecked+=CheatLockChanged;cheatFilter.SelectionChanged+=CheatFilterChanged;CheatFilterChanged(null,null!);
        cheatTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(700)};cheatTimer.Tick+=async(_,_)=>{if(!cheatBusy)await RefreshCheatPanel();};
        return body;
    }
    async Task ScanCheats(bool first) {
        string type=(string)cheatType.SelectedValue,filter=(string)cheatFilter.SelectedValue,value=cheatValue.Text,upper=cheatUpper.Text;bool aligned=cheatAligned.IsChecked==true;int? region=(cheatRegion.SelectedItem as RamChoice)?.Index;
        await CheatUi(async(s,t)=>{cheatMessage.Text="Scanning DOS RAM…";if(!first&&(s.Search.Type!=type||s.Search.Aligned!=aligned||s.Search.Region!=region))throw new ArgumentException("Use First scan after changing the type, alignment or region.");await s.Scan(first,type,filter,value,upper,aligned,region,t);cheatPage=0;cheatMessage.Text=s.Search.Count.ToString("N0")+" matches · narrow the results after the value changes.";});
    }
    void CheatFilterChanged(object? sender,SelectionChangedEventArgs e) {string filter=(string?)cheatFilter.SelectedValue??"exact";cheatValue.IsEnabled=filter is not ("unknown" or "changed" or "unchanged" or "increased" or "decreased");cheatUpper.IsEnabled=filter=="between";}
    void ShowCheatResults() {if(cheatSession==null)return;var search=cheatSession.Search;if(ReferenceEquals(renderedCheatSearch,search)&&renderedCheatScan==search.ScanNumber&&renderedCheatPage==cheatPage)return;
        cheatResults.ItemsSource=search.Results(cheatPage*200,200);cheatCount.Text=search.Count.ToString("N0")+" matches · page "+(cheatPage+1);renderedCheatSearch=search;renderedCheatScan=search.ScanNumber;renderedCheatPage=cheatPage;}
    async Task AddCheatResult() => await CheatUi(async(s,t)=>{if(cheatResults.SelectedItem is not CheatResult result)throw new ArgumentException("Select a search result.");var watch=await s.Add("Value",result.Address,result.Type,t);cheatWatches.Items.Refresh();cheatWatches.SelectedItem=watch;cheatTabs.SelectedIndex=1;cheatMessage.Text="Watch added. Enter a target, then Apply or Lock.";});
    async Task EditCheatWatch() {if(cheatWatches.SelectedItem is not CheatWatch w){cheatMessage.Text="Select a watch.";return;}string name=cheatName.Text,value=cheatTarget.Text;bool locked=cheatLock.IsChecked==true;await CheatUi(async(s,t)=>await s.Update(w.Id,name,value,locked,true,t));}
    void CheatWatchSelected(object? sender,SelectionChangedEventArgs e) {if(cheatName.IsKeyboardFocusWithin||cheatTarget.IsKeyboardFocusWithin)return;loadingCheat=true;if(cheatWatches.SelectedItem is CheatWatch w){cheatName.Text=w.Name;cheatTarget.Text=w.Target;cheatLock.IsChecked=w.Frozen;cheatWatchDetails.Text=w.AddressText+" · "+w.Type+" · "+w.PointerText;}else{cheatLock.IsChecked=false;cheatWatchDetails.Text="Select a watch to edit or lock it.";}loadingCheat=false;}
    async void CheatLockChanged(object sender,RoutedEventArgs e) {
        if(loadingCheat)return;
        if(cheatWatches.SelectedItem is not CheatWatch watch){loadingCheat=true;cheatLock.IsChecked=false;loadingCheat=false;return;}
        var expected=cheatSession;bool frozen=cheatLock.IsChecked==true;string? target=frozen?cheatTarget.Text:null;pendingCheatLocks++;
        try { await CheatOperation(async(s,t)=>{if(!ReferenceEquals(s,expected))throw new InvalidOperationException("DOS session changed.");await s.Update(watch.Id,null,target,frozen,false,t);return true;},CancellationToken.None); }
        catch(Exception error){cheatMessage.Text=error.Message;}
        finally {pendingCheatLocks--;if(pendingCheatLocks==0){loadingCheat=true;if(pendingCheatLocks==0)cheatLock.IsChecked=(cheatWatches.SelectedItem as CheatWatch)?.Frozen==true;loadingCheat=false;}await RefreshCheatPanel();}
    }
    async Task RefreshCheatPanel() {
        if(!await cheatActions.WaitAsync(0))return;
        try {
            if(core is not {IsReady:true}){cheatContext.Text="No game running";cheatMessage.Text="Start a DOS program to search or edit RAM.";cheatSession?.Sync();cheatWatches.Items.Refresh();return;}
            cheatContext.Text="Running: "+running?.Name+" · "+System.IO.Path.GetFileName(running?.Startup??"");cheatContext.ToolTip=running?.Startup;
            var s=Cheats();var regions=await s.Core.GetRamRegions(CancellationToken.None);
            var choices=regions.Select(r=>new RamChoice(r.Index,$"{r.Name} · {r.Length/1024:N0} KiB · 0x{r.Start:X}")).Concat([new RamChoice(null,"All mapped RAM (up to 64 MiB)")]).ToArray();
            if(cheatRegion.ItemsSource is not RamChoice[] prior||!prior.SequenceEqual(choices)){int? selected=(cheatRegion.SelectedItem as RamChoice)?.Index??0;cheatRegion.ItemsSource=choices;cheatRegion.SelectedItem=choices.FirstOrDefault(c=>c.Index==selected)??choices[0];}
            await s.Refresh(CancellationToken.None);cheatWatches.Items.Refresh();ShowCheatResults();ShowPointerResults(s);if(cheatWatches.SelectedItem is CheatWatch detail)cheatWatchDetails.Text=detail.AddressText+" · "+detail.Type+" · "+detail.PointerText;loadingCheat=true;if(pendingCheatLocks==0)cheatLock.IsChecked=(cheatWatches.SelectedItem as CheatWatch)?.Frozen==true;loadingCheat=false;
        } catch(Exception e){cheatMessage.Text=e.Message;}finally{cheatActions.Release();}
    }
}
