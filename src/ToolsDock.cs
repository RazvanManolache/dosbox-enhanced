using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    readonly TabControl toolsTabs=new(){Margin=new Thickness(9,0,6,0)};
    readonly TabControl cheatTabs=new(){Margin=new Thickness(0,4,0,0)};
    readonly GridSplitter toolsSplitter=new(){Width=6,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Stretch,ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndCurrent,Background=Brush(57,72,91),ShowsPreview=false,ToolTip="Drag to resize the right panel; double-click to reset",Cursor=Cursors.SizeWE};
    FrameworkElement? cheatPanel;
    void BuildToolsDock(FrameworkElement dlss){
        toolsTabs.Items.Add(new TabItem{Header="DLSS",Content=dlss});
        cheatPanel=BuildCheatPanel();toolsTabs.Items.Add(new TabItem{Header="Cheats",Content=cheatPanel});
        toolsTabs.SelectedIndex=preferences.ToolsTab=="cheats"?1:0;
        Grid.SetColumn(toolsTabs,2);deck.Children.Add(toolsTabs);
        Grid.SetColumn(toolsSplitter,2);Panel.SetZIndex(toolsSplitter,2);deck.Children.Add(toolsSplitter);
        toolsTabs.SelectionChanged+=(_,e)=>{if(!ReferenceEquals(e.Source,toolsTabs))return;ReleaseMouseCapture();core?.ReleaseKeys();preferences.ToolsTab=toolsTabs.SelectedIndex==1?"cheats":"dlss";SavePanePreferences();UpdateToolsDock();UpdatePaneButtons();};
        toolsSplitter.DragStarted+=(_,_)=>{ReleaseMouseCapture();core?.ReleaseKeys();};
        toolsSplitter.DragCompleted+=(_,_)=>SetToolsWidth(deck.ColumnDefinitions[2].ActualWidth);
        toolsSplitter.MouseDoubleClick+=(_,_)=>SetToolsWidth(480);
    }
    void SetToolsWidth(double width){preferences.ToolsWidth=double.IsFinite(width)?Math.Clamp(width,360,900):480;ApplyToolsWidth();preferencesTimer.Stop();preferencesTimer.Start();}
    void ApplyToolsWidth(){
        if(deck.ColumnDefinitions.Count<3)return;
        var column=deck.ColumnDefinitions[2];double available=Math.Max(360,(ActualWidth>0?ActualWidth:Width)-(environmentVisible?285:0)-340);
        column.MinWidth=effectsVisible?360:0;column.MaxWidth=effectsVisible?Math.Min(900,available):double.PositiveInfinity;
        double desired=double.IsFinite(preferences.ToolsWidth)?Math.Clamp(preferences.ToolsWidth,360,900):480;
        column.Width=new GridLength(effectsVisible?Math.Min(desired,column.MaxWidth):0);
    }
    void ShowToolTab(int index,bool toggle=false){
        ReleaseMouseCapture();core?.ReleaseKeys();
        if(toggle&&effectsVisible&&toolsTabs.SelectedIndex==index)effectsVisible=false;
        else{effectsVisible=true;toolsTabs.SelectedIndex=index;}
        SavePanePreferences();ApplyLayout();if(fullscreen&&effectsVisible)ShowMenu(true);
    }
    void UpdateToolsDock(){
        toolsSplitter.Visibility=effectsVisible?Visibility.Visible:Visibility.Collapsed;
        if(effectsVisible&&toolsTabs.SelectedIndex==1){cheatTimer?.Start();_=RefreshCheatPanel();}else cheatTimer?.Stop();
    }
    static FrameworkElement CheatFields(params (string Label,FrameworkElement Control)[] fields){
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());
        for(int i=0;i<fields.Length;i++){
            if(i%2==0)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var field=new StackPanel{Margin=new Thickness(0,0,i%2==0?8:0,2)};field.Children.Add(Label(fields[i].Label,12));
            var control=fields[i].Control;control.Width=double.NaN;control.HorizontalAlignment=HorizontalAlignment.Stretch;field.Children.Add(control);
            Grid.SetColumn(field,i%2);Grid.SetRow(field,i/2);grid.Children.Add(field);
        }return grid;
    }
    Button CheatButton(string label,Action action){var button=Button(label,action);button.Padding=new Thickness(7,4,7,4);button.Margin=new Thickness(2);button.FontSize=12;return button;}
    static Expander CheatHelp(string title,string text)=>new(){Header=title,Content=Label(text,12),Margin=new Thickness(0,4,0,3),Foreground=Brush(175,202,223)};
    static FrameworkElement CheatWorkspace(FrameworkElement controls,FrameworkElement list,FrameworkElement? footer=null){
        var grid=new Grid{Margin=new Thickness(7)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var scroll=new ScrollViewer{Content=controls,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxHeight=290};grid.Children.Add(scroll);
        list.MinHeight=80;Grid.SetRow(list,1);grid.Children.Add(list);
        if(footer!=null){Grid.SetRow(footer,2);grid.Children.Add(footer);}
        void FitControls()=>scroll.MaxHeight=Math.Max(60,Math.Min(grid.ActualHeight*.6,grid.ActualHeight-100-(footer?.ActualHeight??0)));
        grid.SizeChanged+=(_,_)=>FitControls();if(footer!=null)footer.SizeChanged+=(_,_)=>FitControls();return grid;
    }
}
