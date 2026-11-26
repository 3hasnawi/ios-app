using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AnbarPayroll;

internal static class WindowsUpdateChecks
{
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler{
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));
    }
    public static async Task Run(string dir,Action<bool,string> check){
        byte[] original="synthetic installer"u8.ToArray();
        byte[] responseBytes=original;
        string tag="windows-v0.10.0",baseUrl="https://github.com/3hasnawi/ios-app/releases/download/";
        string digest="sha256:"+Convert.ToHexString(SHA256.HashData(original));
        string assetUrl=baseUrl+tag+"/"+WindowsRelease.FileName;
        HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
        bool fail=false;
        using var client=new HttpClient(new Handler(request=>{
            var path=request.RequestUri!.AbsolutePath;
            if(fail)return new(HttpStatusCode.ServiceUnavailable);
            if(path.Contains("matching-refs"))return Json(new[]{"build-140","windows-v0.10.0","windows-v0.9.0","windows-v0.12.0-beta","windows-v0.11.0","windows-v3.0.0"}.Select(t=>new{ @ref="refs/tags/"+t}));
            if(path.EndsWith("windows-v3.0.0"))return new(HttpStatusCode.NotFound);
            if(path.EndsWith("windows-v0.11.0"))return Json(new{tag_name="windows-v0.11.0",draft=false,prerelease=true});
            if(path.EndsWith(tag))return Json(new{tag_name=tag,draft=false,prerelease=false,body="تعديل تجريبي",assets=new[]{new{name=WindowsRelease.FileName,state="uploaded",digest,size=original.Length,browser_download_url=assetUrl}}});
            if(request.RequestUri.AbsoluteUri==baseUrl+tag+"/"+WindowsRelease.FileName)return new(HttpStatusCode.OK){Content=new ByteArrayContent(responseBytes)};
            throw new Exception("Unexpected update request "+path);
        }));
        var api=new WindowsUpdates(client);
        var update=await api.LatestAfter(new(0,9,0),default);
        check(update?.Version==new Version(0,10,0)&&update.Notes=="تعديل تجريبي","newest published Windows release by numeric version; beta, tags without releases and iPhone tags ignored");
        check(await api.LatestAfter(new(0,10,0),default)==null,"no update for the installed version");
        var folder=Path.Combine(dir,"updates");
        string saved=await api.Download(update!,folder,null,default);
        check(File.ReadAllBytes(saved).SequenceEqual(original),"verified installer downloads from the release asset");
        responseBytes=original.Reverse().ToArray();
        try{await api.Download(update!,folder,null,default);throw new Exception("tampered installer accepted");}catch(InvalidDataException){}
        responseBytes=original[..^1];
        try{await api.Download(update!,folder,null,default);throw new Exception("partial installer accepted");}catch(InvalidDataException){}
        check(File.ReadAllBytes(saved).SequenceEqual(original)&&!Directory.EnumerateFiles(folder,"*.part").Any(),"failed downloads preserve the verified file and discard partial contents");
        assetUrl="https://example.org/untrusted.exe";
        try{await api.LatestAfter(new(0,1,0),default);throw new Exception("external URL accepted");}catch(InvalidDataException){}
        fail=true;
        try{await api.LatestAfter(new(0,1,0),default);throw new Exception("offline reported as current");}catch(HttpRequestException){check(true,"offline checks and invalid asset URLs never start installation");}
    }
}
