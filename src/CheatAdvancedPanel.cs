using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    readonly TextBox pointerTarget=new(){Width=120,Text="0x0"},pointerDepth=new(){Width=45,Text="3"},pointerOffset=new(){Width=75,Text="256"},pointerSegment=new(){Width=85,Text="0x0"};
    readonly ComboBox pointerMode=new(){Width=210,ItemsSource=new[]{new CheatChoice("physical32","32-bit flat DOS address"),new CheatChoice("far16","16:16 segment:offset"),new CheatChoice("near16","16-bit offset + segment"),new CheatChoice("mapped32","32-bit core mapped address")},DisplayMemberPath="Name",SelectedValuePath="Id",SelectedIndex=0};
    readonly CheckBox pointerAligned=new(){Content="Aligned",IsChecked=true},pointerNegative=new(){Content="Negative offsets"},pointerWrap=new(){Content="20-bit wrap (A20 off)"};
    readonly ListView pointerResults=CheatList(("Root slot","BaseText",105),("Offsets →","OffsetsText",210),("Depth","Depth",50));
    readonly TextBlock pointerStatus=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(6),Foreground=Brush(221,231,241)};
    PointerScanState? renderedPointers;
    DosCore? disassembledCore;
    long disassembledRevision;
    readonly TextBox disasmAddress=new(){Width=120,Text="0x0"},disasmIP=new(){Width=100,Text="0x100"};
    readonly ComboBox disasmBits=new(){Width=120,ItemsSource=new[]{16,32},SelectedIndex=0};
    readonly ListView disasmResults=CheatList(("Mapped RAM","AddressText",100),("Instruction","Instruction",230),("Bytes","Bytes",100));
    long SelectedCheatAddress(bool watch)=>watch?(cheatWatches.SelectedItem as CheatWatch)?.Address??throw new ArgumentException("Select a watch first."):(cheatResults.SelectedItem as CheatResult)?.Address??throw new ArgumentException("Select a search result first.");
    void UseSelectedAddress(TextBox destination,bool watch){try{destination.Text="0x"+SelectedCheatAddress(watch).ToString("X");}catch(Exception e){cheatMessage.Text=e.Message;}}
    static int CheatNumber(TextBox box)=>checked((int)CheatValue.Address(box.Text));
    internal static ulong DisassemblyIP(string text,int bits){text=text.Trim();bool hex=text.StartsWith("0x",StringComparison.OrdinalIgnoreCase);if(!ulong.TryParse(hex?text[2..]:text,hex?NumberStyles.HexNumber:NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong value)||value>(bits==16?65535UL:uint.MaxValue))throw new ArgumentException("Logical IP must fit the selected 16/32-bit mode.");return value;}
    void AddAdvancedCheatTabs(TabControl tabs){
        var controls=new StackPanel();pointerTarget.Width=double.NaN;controls.Children.Add(Label("Target address (mapped RAM)",12));controls.Children.Add(pointerTarget);
        var row=new WrapPanel();row.Children.Add(CheatButton("Use search result",()=>UseSelectedAddress(pointerTarget,false)));row.Children.Add(CheatButton("Use watch",()=>UseSelectedAddress(pointerTarget,true)));controls.Children.Add(row);
        pointerMode.Width=double.NaN;controls.Children.Add(Label("Pointer format",12));controls.Children.Add(pointerMode);
        var advanced=new StackPanel();advanced.Children.Add(CheatFields(("Maximum depth",pointerDepth),("Maximum offset",pointerOffset),("Near segment",pointerSegment)));advanced.Children.Add(pointerAligned);advanced.Children.Add(pointerNegative);advanced.Children.Add(pointerWrap);
        advanced.Children.Add(Label("Near pointers need the correct segment. Flat pointers require identity-mapped DOS addresses. Paging/selectors are not translated; 20-bit wrap is manual.",12));controls.Children.Add(new Expander{Header="Scan options · depth, offsets, segment",Content=advanced,Foreground=Foreground});
        row=new WrapPanel();row.Children.Add(CheatButton("Find chains",()=>_=ScanPointerUi(false)));row.Children.Add(CheatButton("Rescan",()=>_=ScanPointerUi(true)));row.Children.Add(CheatButton("Cancel",()=>cheatCancellation?.Cancel()));row.Children.Add(CheatButton("Clear",()=>_=CheatUi((s,t)=>{s.ClearPointers();return Task.CompletedTask;})));var scanActions=row;
        controls.Children.Add(CheatHelp("How to rescan","Find the value again after a restart or relocation. Enter its new address, then Rescan to keep only matching saved chains. Candidates are saved per game. Rescan uses the original options."));
        var footer=new StackPanel();footer.Children.Add(scanActions);footer.Children.Add(pointerStatus);footer.Children.Add(CheatButton("Watch selected chain",()=>_=AddPointerUi()));
        tabs.Items.Add(new TabItem{Header="Pointers",ToolTip="Pointer chains",Content=CheatWorkspace(controls,pointerResults,footer)});
        controls=new StackPanel();disasmAddress.Width=double.NaN;controls.Children.Add(Label("Start address (mapped RAM)",12));controls.Children.Add(disasmAddress);
        row=new WrapPanel();row.Children.Add(CheatButton("Use search result",()=>UseSelectedAddress(disasmAddress,false)));row.Children.Add(CheatButton("Use watch",()=>UseSelectedAddress(disasmAddress,true)));controls.Children.Add(row);
        controls.Children.Add(CheatFields(("CPU mode (bits)",disasmBits),("Logical IP",disasmIP)));
        row=new WrapPanel();row.Children.Add(CheatButton("Decode / refresh",()=>_=DisassembleUi()));row.Children.Add(CheatButton("Next page →",()=>{if(disasmResults.ItemsSource is DisassemblyLine[] lines&&lines.Length>0){var last=lines[^1];disasmAddress.Text="0x"+(last.Address+last.Bytes.Length/2).ToString("X");disasmIP.Text="0x"+((last.IP+(ulong)last.Bytes.Length/2)&((int)disasmBits.SelectedItem==16?65535UL:uint.MaxValue)).ToString("X");_=DisassembleUi();}}));var decodeActions=row;
        controls.Children.Add(CheatHelp("About CPU mode and addresses","16-bit real-mode or 32-bit protected-mode decoding is manual. Logical IP controls branch labels (COM entry normally 0x100). Pause for stable code. Read-only; no CPU registers, stepping, paging/selector translation or automatic code/data detection."));
        var detail=new TextBlock{Text="Select an instruction for its full bytes, physical address and flow.",TextWrapping=TextWrapping.Wrap,Foreground=Brush(175,202,223),Margin=new Thickness(0,6,0,0),MaxHeight=100};
        disasmResults.SelectionChanged+=(_,_)=>detail.Text=disasmResults.SelectedItem is DisassemblyLine line?$"IP {line.IPText} · physical {line.PhysicalText} · {line.Flow}\n{line.Bytes}\n{line.Instruction}":"Select an instruction for details.";
        var codeFooter=new StackPanel();codeFooter.Children.Add(decodeActions);codeFooter.Children.Add(detail);
        tabs.Items.Add(new TabItem{Header="Disassembly",ToolTip="Read-only x86 disassembly",Content=CheatWorkspace(controls,disasmResults,codeFooter)});
    }
    async Task ScanPointerUi(bool rescan)=>await CheatUi(async(s,t)=>{
        long target=CheatValue.Address(pointerTarget.Text);var options=new PointerOptions((string)pointerMode.SelectedValue,CheatNumber(pointerDepth),CheatNumber(pointerOffset),pointerNegative.IsChecked==true,pointerAligned.IsChecked==true,(cheatRegion.SelectedItem as RamChoice)?.Index,CheatNumber(pointerSegment),pointerWrap.IsChecked==true);
        cheatMessage.Text=rescan?"Checking saved chains…":"Searching pointer chains…";await s.ScanPointers(target,options,rescan,t);ShowPointerResults(s);cheatMessage.Text="Pointer scan complete. Rescan against a newly located target to eliminate unstable chains.";
    });
    void ShowPointerResults(CheatSession session){if(disassembledCore!=null&&(!ReferenceEquals(disassembledCore,session.Core)||disassembledRevision!=session.Revision)){disasmResults.ItemsSource=null;disassembledCore=null;}if(ReferenceEquals(renderedPointers,session.Pointers)&&pointerStatus.Text.Length>0)return;renderedPointers=session.Pointers;pointerResults.ItemsSource=session.Pointers?.Candidates;pointerStatus.Text=session.Pointers is {} p?$"{p.Candidates.Length:N0} chains · {p.Options.Mode} · target 0x{p.Target:X}"+(p.Limited?" · PARTIAL: "+p.LimitReason+" reached. Narrow the region, offset or depth.":""):"No pointer results. Find a target with Search first.";}
    async Task AddPointerUi()=>await CheatUi(async(s,t)=>{if(pointerResults.SelectedItem is not PointerCandidate selected||s.Pointers==null)throw new ArgumentException("Select a pointer chain.");int index=Array.IndexOf(s.Pointers.Candidates,selected);var watch=await s.AddPointer(index,"Pointer value",(string)cheatType.SelectedValue,t);cheatWatches.Items.Refresh();cheatWatches.SelectedItem=watch;cheatTabs.SelectedIndex=1;cheatMessage.Text="Pointer watch added; it follows the chain. "+selected.Path.Description;});
    async Task DisassembleUi()=>await CheatUi(async(s,t)=>{int bits=(int)disasmBits.SelectedItem;var result=await s.Core.Disassemble(CheatValue.Address(disasmAddress.Text),bits,DisassemblyIP(disasmIP.Text,bits),128,2048,t);disasmResults.ItemsSource=result.Instructions;disassembledCore=s.Core;disassembledRevision=s.Revision;cheatMessage.Text=$"{result.Instructions.Length} instructions · {bits}-bit · select a row for details.";});
}
