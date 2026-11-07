using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
namespace Ripley;

public sealed class RipFile : INotifyPropertyChanged
{
    public int Number { get; init; }
    public string OriginalName { get; set; } = "";
    string name="Waiting for MakeMKV filename",status="Waiting";
    double progress;
    long bytes;
    public string Name {get=>name;set{name=value;Changed();}}
    public string Status {get=>status;set{status=value;Changed();}}
    public double Progress {get=>progress;set{progress=value;Changed();}}
    public long Bytes {get=>bytes;set{bytes=value;Changed();Changed(nameof(Size));}}
    public string Size=>Bytes>=1L<<30?$"{Bytes/(double)(1L<<30):0.00} GB":$"{Bytes/(double)(1L<<20):0.0} MB";
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}

public sealed class RipQueue
{
    public ObservableCollection<RipFile> Files {get;} = new();
    public double Overall {get;private set;}
    public string Phase {get;private set;}="Ready to rip";
    public int CurrentIndex {get;private set;}=-1;
    bool saving,writing;
    public RipFile? Current=>CurrentIndex>=0&&CurrentIndex<Files.Count?Files[CurrentIndex]:null;
    public string Summary=>$"{Files.Count} {(Files.Count==1?"file":"files")} · {Files.Count(f=>f.Status=="Saved")} saved · {Files.Count(f=>f.Status=="Written")} written · {Files.Count(f=>f.Status=="Waiting")} waiting · {Overall:0}% overall";
    public string Activity=>Current is { } file?$"File {file.Number} of {Files.Count} · {file.Name} · {file.Progress:0}% · {Phase}":Phase;

    public void Reset(int count)
    {
        Files.Clear();CurrentIndex=-1;Overall=0;saving=writing=false;Phase="Ready to rip";
        SetCount(count);
    }
    public void Restore(IEnumerable<RipFile> files,double overall,string phase)
    {
        Reset(0);
        foreach(var file in files)Files.Add(new RipFile{Number=file.Number,OriginalName=file.OriginalName,Name=file.Name,Status=file.Status,Progress=file.Progress,Bytes=file.Bytes});
        Overall=overall;Phase=phase;
    }
    void SetCount(int count)
    {
        if(count<0||count>10000) return;
        while(Files.Count<count) Files.Add(new RipFile{Number=Files.Count+1});
        while(Files.Count>count&&Files[^1].Status=="Waiting") Files.RemoveAt(Files.Count-1);
    }
    public void Read(string line)
    {
        var colon=line.IndexOf(':');if(colon<0)return;
        var fields=Disc.Fields(line[(colon+1)..]);
        // MakeMKV robot protocol: PRGV=current,total,max; PRGC=code,operation sub-id,name.
        // https://www.makemkv.com/developers/usage.txt
        switch(line[..colon]) {
            case "MSG" when fields.Length>=6&&fields[0]=="5014"&&int.TryParse(fields[5],out var count):
                SetCount(count);saving=true;Overall=0;break;
            case "PRGT" when fields.Length>=3:
                Phase=fields[2];
                if(fields[0]=="5024") {saving=true;Overall=0;}
                break;
            case "PRGC" when fields.Length>=3:
                Phase=fields[2];writing=false;
                if(saving&&fields[0] is "5017" or "5057"&&int.TryParse(fields[1],out var index)&&index>=0&&index<10000) {
                    if(Current is {Status:"Ripping"} previous&&CurrentIndex!=index) {previous.Status="Written";previous.Progress=100;}
                    SetCount(Math.Max(Files.Count,index+1));CurrentIndex=index;
                    Current!.Status="Ripping";writing=fields[0]=="5017";
                }
                break;
            case "PRGV" when saving&&fields.Length>=3:
                if(double.TryParse(fields[0],NumberStyles.Float,CultureInfo.InvariantCulture,out var current)&&
                   double.TryParse(fields[1],NumberStyles.Float,CultureInfo.InvariantCulture,out var total)&&
                   double.TryParse(fields[2],NumberStyles.Float,CultureInfo.InvariantCulture,out var max)&&max>0&&double.IsFinite(max)&&double.IsFinite(current)&&double.IsFinite(total)) {
                    Overall=Math.Clamp(total/max*100,0,100);
                    if(writing&&Current is { } file) file.Progress=Math.Clamp(current/max*100,0,100);
                }
                break;
        }
    }
    public void ObserveFiles(IEnumerable<(string Name,long Bytes)> observed,Func<string,string> displayName)
    {
        foreach(var item in observed) {
            var file=Files.FirstOrDefault(f=>f.OriginalName.Equals(item.Name,StringComparison.OrdinalIgnoreCase));
            file??=Files.Take(CurrentIndex+1).FirstOrDefault(f=>f.OriginalName.Length==0);
            if(file==null) continue;
            file.OriginalName=item.Name;file.Name=displayName(item.Name);file.Bytes=item.Bytes;
        }
    }
    public void Publishing() {Phase="Applying filenames and confirming raw files";writing=false;}
    public void Complete(IReadOnlyList<Job> jobs)
    {
        foreach(var job in jobs) {
            var file=Files.FirstOrDefault(f=>f.OriginalName.Equals(job.OriginalFilename,StringComparison.OrdinalIgnoreCase))??Files.FirstOrDefault(f=>f.OriginalName.Length==0);
            if(file==null) {file=new RipFile{Number=Files.Count+1};Files.Add(file);}
            file.OriginalName=job.OriginalFilename;file.Name=System.IO.Path.GetFileName(job.Source);file.Bytes=job.SourceSize;file.Progress=100;file.Status="Saved";
        }
        foreach(var file in Files.Where(f=>f.Status!="Saved")) file.Status="Not produced";
        Overall=100;Phase="Rip complete · raw files saved";saving=writing=false;CurrentIndex=-1;
    }
    public void Stop(bool cancelled)
    {
        foreach(var file in Files.Where(f=>f.Status!="Saved")) file.Status=file.OriginalName.Length>0?"Kept · unverified":"Not started";
        Phase=cancelled?"Rip cancelled · raw files kept":"Rip failed · raw files kept";saving=writing=false;CurrentIndex=-1;
    }
}
