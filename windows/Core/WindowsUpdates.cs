using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnbarPayroll;

public sealed record WindowsRelease(Version Version,string Notes,long Bytes,string Sha256)
{
    public const string FileName="AnbarPayroll-Setup.exe";
    public static string DisplayVersion(Version version) =>
        version.Major >= 1 && version.Build <= 0 ? version.ToString(2) : version.ToString();

    public Uri Installer=>new($"https://github.com/3hasnawi/ios-app/releases/download/windows-v{Version}/{FileName}");
}

public sealed class WindowsUpdates(HttpClient client)
{
    const string Api="https://api.github.com/repos/3hasnawi/ios-app";
    const string RefPrefix="refs/tags/windows-v";

    async Task<JsonDocument?> Read(string url,CancellationToken token,bool allowMissing=false)
    {
        using var response=await client.GetAsync(url,token).ConfigureAwait(false);
        if(allowMissing&&response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
    }
    public async Task<WindowsRelease?> LatestAfter(Version installed,CancellationToken token)
    {
        using var refs=await Read(Api+"/git/matching-refs/tags/windows-v",token).ConfigureAwait(false);
        var candidates=new SortedSet<Version>();
        foreach(var item in refs!.RootElement.EnumerateArray()){
            var name=item.GetProperty("ref").GetString()??"";
            if(name.StartsWith(RefPrefix,StringComparison.Ordinal)&&Version.TryParse(name[RefPrefix.Length..],out var version)
                &&version.Build>=0&&version.Revision<0&&name==RefPrefix+version&&version>installed)candidates.Add(version);
        }
        foreach(var version in candidates.Reverse()){
            string tag="windows-v"+version;
            using var release=await Read(Api+"/releases/tags/"+tag,token,true).ConfigureAwait(false);
            if(release==null)continue;
            var info=release.RootElement;
            if(info.GetProperty("tag_name").GetString()!=tag||info.GetProperty("draft").GetBoolean()||info.GetProperty("prerelease").GetBoolean())continue;
            foreach(var asset in info.GetProperty("assets").EnumerateArray()){
                if(asset.GetProperty("name").GetString()!=WindowsRelease.FileName||asset.GetProperty("state").GetString()!="uploaded")continue;
                string digest=asset.TryGetProperty("digest",out var field)?field.GetString()??"":"";
                var bytes=asset.GetProperty("size").GetInt64();
                if(!digest.StartsWith("sha256:",StringComparison.Ordinal)||digest.Length!=71||!digest[7..].All(Uri.IsHexDigit)||bytes is <=0 or >512*1024*1024)
                    throw new InvalidDataException("تعذّر التحقق من بيانات المثبّت المنشور.");
                var result=new WindowsRelease(version,info.GetProperty("body").GetString()??"",bytes,digest[7..]);
                if(asset.GetProperty("browser_download_url").GetString()!=result.Installer.AbsoluteUri)
                    throw new InvalidDataException("رابط المثبّت لا يطابق إصدار ويندوز.");
                return result;
            }
        }
        return null;
    }
    public async Task<string> Download(WindowsRelease release,string folder,IProgress<int>? progress,CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        string destination=Path.Combine(folder,WindowsRelease.FileName),partial=Path.Combine(folder,Guid.NewGuid()+".part");
        try{
            using var response=await client.GetAsync(release.Installer,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long advertised&&advertised!=release.Bytes)throw new InvalidDataException("حجم التنزيل لا يطابق الإصدار.");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            long written=0;int last=-1;
            using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)){
                var buffer=new byte[81920];int count;
                while((count=await stream.ReadAsync(buffer,token).ConfigureAwait(false))!=0){
                    written+=count;if(written>release.Bytes)throw new InvalidDataException("تجاوز التنزيل حجم المثبّت المعلن.");
                    hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);
                    int percent=(int)(written*100/release.Bytes);
                    if(percent!=last){last=percent;progress?.Report(percent);}
                }
            }
            if(written!=release.Bytes||!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("فشل التحقق من سلامة ملف التحديث. أعد تنزيله.");
            token.ThrowIfCancellationRequested();File.Move(partial,destination,true);return destination;
        }finally{File.Delete(partial);}
    }
}
