using System.Buffers.Binary;
using System.Text;

namespace AnbarPayroll;

/// Swift compares the exact IEEE-754 timestamp in the compressed batch with the one in its plist.
/// NSDate round-trips through DateTime ticks in plist-cil. Preserve the original bits of all
/// existing audit dates, including prepared/confirmed timestamps used during restore checks.
internal sealed class ContractDates
{
    readonly byte[] bytes;
    readonly int refWidth,offsetWidth,count,table,root;
    ContractDates(byte[] data){
        bytes=data;if(bytes.Length<40||!bytes.AsSpan(0,8).SequenceEqual("bplist00"u8))throw new InvalidDataException("صيغة سجل فصل غير صالحة.");
        int end=bytes.Length-32;offsetWidth=bytes[end+6];refWidth=bytes[end+7];count=Read(end+8,8);root=Read(end+16,8);table=Read(end+24,8);
        if(count<1||refWidth is <1 or >8||offsetWidth is <1 or >8||table<8||(long)table+(long)count*offsetWidth>end)throw new InvalidDataException();
    }
    int Read(int pos,int n){if(n is <1 or >8||pos<0||pos>bytes.Length-n)throw new InvalidDataException();int value=0;for(int i=0;i<n;i++)value=checked(value*256+bytes[pos+i]);return value;}
    int Offset(int id){if(id<0||id>=count)throw new InvalidDataException();int pos=Read(table+id*offsetWidth,offsetWidth);if(pos<8||pos>=table)throw new InvalidDataException();return pos;}
    (int Pos,int Length) Header(int id,int type){
        int pos=Offset(id);int tag=bytes[pos++];if(tag>>4!=type)throw new InvalidDataException();
        int length=tag&15;if(length==15){int t=bytes[pos++];if(t>>4!=1||(t&15)>3)throw new InvalidDataException();int width=1<<(t&15);length=Read(pos,width);pos+=width;}
        return(pos,length);
    }
    string Str(int id){int type=bytes[Offset(id)]>>4;if(type is not (5 or 6 or 7))throw new InvalidDataException();var (p,n)=Header(id,type);return(type==6?Encoding.BigEndianUnicode:Encoding.UTF8).GetString(bytes,p,checked(n*(type==6?2:1)));}
    Dictionary<string,int> Dict(int id){
        var (p,n)=Header(id,13);if((long)p+2L*n*refWidth>table)throw new InvalidDataException();var values=new Dictionary<string,int>();
        for(int i=0;i<n;i++)values.Add(Str(Read(p+refWidth*i,refWidth)),Read(p+refWidth*(i+n),refWidth));return values;
    }
    Dictionary<(string,string),int> Locations(){
        var (p,n)=Header(Dict(root)["records"],10);var dates=new Dictionary<(string,string),int>();if((long)p+(long)n*refWidth>table)throw new InvalidDataException();
        for(int i=0;i<n;i++){
            var record=Dict(Read(p+refWidth*i,refWidth));string id=Str(record["id"]);
            void Date(string key,int obj){int address=Offset(obj);if(bytes[address]!=0x33||address+9>table)throw new InvalidDataException();dates.Add((id,key),address+1);}
            foreach(var key in new[]{"importedAt","cancelledAt","reviewedImport"})if(record.TryGetValue(key,out int d))Date(key,d);
            foreach(var field in new[]{"prepared","confirmed"})foreach(var (key,obj) in Dict(record[field]))Date(field+"|"+key,obj);
        }
        return dates;
    }
    internal static Dictionary<(string,string),long> Values(byte[] source)=>new ContractDates(source).Locations().ToDictionary(x=>x.Key,x=>BinaryPrimitives.ReadInt64BigEndian(source.AsSpan(x.Value,8)));
    internal static void Restore(byte[] target,Dictionary<(string,string),long> values){
        var written=new Dictionary<int,long>();
        foreach(var (key,address) in new ContractDates(target).Locations())if(values.TryGetValue(key,out var bits)){
            if(written.TryGetValue(address,out var previous)&&previous!=bits)throw new InvalidDataException("تعذر حفظ دقة تاريخين في الدفعة.");
            BinaryPrimitives.WriteInt64BigEndian(target.AsSpan(address),bits);written[address]=bits;
        }
    }
}
