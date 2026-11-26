using System.Buffers.Binary;
using System.Text;

namespace AnbarPayroll;

// Foundation stores each preference as a separate top-level binary property list.
internal static class PlistData
{
    private static int Number(ReadOnlySpan<byte> b){int n=0;if(b.Length is <1 or >8)throw new InvalidDataException();foreach(var v in b)n=checked(n*256+v);return n;}
    private static (int Kind, int Offset,int Count) Root(byte[] b){
        if(b.Length<41||!b.AsSpan(0,8).SequenceEqual("bplist00"u8))throw new InvalidDataException("صيغة إعدادات الآيفون غير صالحة.");
        var trailer=b.AsSpan(b.Length-32);
        var width=trailer[6];int top=Number(trailer.Slice(16,8)),table=Number(trailer.Slice(24,8));
        int pos=Number(b.AsSpan(checked(table+top*width),width)),marker=b[pos++],count=marker&15;
        if(count==15){var size=b[pos++];if((size>>4)!=1||(size&15)>3)throw new InvalidDataException();var len=1<<(size&15);count=Number(b.AsSpan(pos,len));pos+=len;}
        return(marker>>4,pos,count);
    }
    public static byte[] Unwrap(byte[] b){var (kind,offset,count)=Root(b);if(kind!=4)throw new InvalidDataException();return b.AsSpan(offset,count).ToArray();}
    public static string String(byte[] b){var (kind,offset,count)=Root(b);return kind switch{5=>Encoding.ASCII.GetString(b,offset,count),6=>Encoding.BigEndianUnicode.GetString(b,offset,checked(count*2)),_=>throw new InvalidDataException()};}
}
