using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Identity.Client;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AnbarPayroll.Desktop;

internal sealed class OneDriveTrialWindow : Window
{
    const string ClientId = "986d159c-3325-4480-af32-3572c0c9f3c9";
    const string Drive = "https://graph.microsoft.com/v1.0/me/drive/";
    const string Prefix = "payroll-trial-v1-";
    const string PageUrl = "https://onedrive-trial.invalid/index.html";
    readonly TextBlock status = new() { Text = "جارٍ فتح شاشة تسجيل الدخول…", Margin = new Thickness(16),
        FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap };
    readonly TaskCompletionSource pageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly WebView2 viewer = new();
    readonly CancellationTokenSource lifetime = new();
    readonly HttpClient network = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1_000_000 };
    const string RefundPrefix = "payroll-refund-trial-v1-";
    readonly Action<Window,string>? openRefund;
    readonly string local;
    string token = "", root = "";
    FileStream? exclusive;
    bool closed;
    readonly bool hindiNumbers;

    public OneDriveTrialWindow(bool hindiNumbers, string? trialDirectory = null, Action<Window,string>? openRefund = null)
    {
        local = trialDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnbarPayroll", "OneDriveTrialV1");
        this.hindiNumbers = hindiNumbers;
        this.openRefund = openRefund;
        Title = "اختبار الربط بـ OneDrive";
        Width = 780; Height = 610;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        panel.Children.Add(status);
        panel.Children.Add(viewer);
        Content = panel;
        Loaded += async (_, _) => await Initialize();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) =>
        {
            closed = true; lifetime.Cancel(); token = "";
            network.Dispose(); viewer.Dispose(); exclusive?.Dispose();
        };
    }

    static string Resource(string extension)
    {
        using var stream = typeof(OneDriveTrialWindow).Assembly.GetManifestResourceStream("OneDriveTrial." + extension)
            ?? throw new InvalidDataException("تعذّر تحميل شاشة التجربة.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    async Task Initialize()
    {
        try
        {
            Directory.CreateDirectory(local);
            // One local outbox per Windows user, even if two app processes are open.
            exclusive = new FileStream(Path.Combine(local,"session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var profile = Path.Combine(local, "WebView");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile).WaitAsync(TimeSpan.FromSeconds(40), lifetime.Token);
            if (closed) return;
            await viewer.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(40), lifetime.Token);
            if (closed) return;
            var web = viewer.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreHostObjectsAllowed = false;
            var html = Resource("html").Replace("data-numbers=\"latn\"", hindiNumbers ? "data-numbers=\"arab\"" : "data-numbers=\"latn\"");
            var document = Encoding.UTF8.GetBytes(html.Replace("/* TRIAL_ENGINE */", Resource("js")));
            // عنوان محلي ثابت للصفحة المضمّنة ولرسائلها. لا يعتمد على عنوان التنقّل الداخلي لـ NavigateToString.
            web.AddWebResourceRequestedFilter(PageUrl, CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.Document);
            web.WebResourceRequested += (_, e) =>
            {
                if (e.Request.Uri == PageUrl && e.Request.Method == "GET")
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(document, writable:false), 200, "OK",
                        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            };
            web.NavigationStarting += (_, e) => e.Cancel = e.Uri != PageUrl;
            web.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && !pageStarted.Task.IsCompleted)
                    pageStarted.TrySetException(new InvalidDataException("تعذّر تحميل صفحة تسجيل الدخول (" + e.WebErrorStatus + ")."));
            };
            web.ProcessFailed += (_, e) =>
            {
                if (!closed && e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                    ShowFailure("توقف عارض WebView2. أغلق نافذة التجربة وافتحها مجدداً.");
            };
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.WebMessageReceived += async (_, e) =>
            {
                if (closed || e.Source != PageUrl) return;
                int n = 0;
                string value = "", error = "";
                try
                {
                    using var json = JsonDocument.Parse(e.WebMessageAsJson);
                    var m = json.RootElement;
                    n = m.GetProperty("n").GetInt32();
                    var action = m.GetProperty("action").GetString()!;
                    if (action == "load") pageStarted.TrySetResult();
                    value = await Perform(action, m.GetProperty("id").GetString()!, m.GetProperty("value").GetString()!);
                }
                catch (MsalClientException) { error = "لم يكتمل تسجيل الدخول عبر متصفح ويندوز."; }
                catch (MsalServiceException ex) { error = "تعذّر تسجيل الدخول لدى Microsoft (" + ex.ErrorCode + ")."; }
                catch (Exception ex) { error = ex is InvalidDataException ? ex.Message : "تعذّر إكمال الطلب. تحقق من الاتصال وحاول مجدداً."; }
                if (!closed) web.PostWebMessageAsJson(JsonSerializer.Serialize(new { n, value, error }));
            };
            web.Navigate(PageUrl);
            await pageStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            if (!closed && viewer.Visibility == Visibility.Visible) status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (!closed)
        {
            var message = ex switch
            {
                InvalidDataException => ex.Message,
                IOException when exclusive == null => "تعذّر فتح مجلد التجربة. تحقق من صلاحية المجلد وأغلق أي نافذة تجربة أخرى.",
                TimeoutException => "لم يستجب عارض WebView2. أغلق نافذة التجربة وافتحها مجدداً.",
                _ => "تعذّر تشغيل شاشة التجربة. تحقق من تثبيت Microsoft Edge WebView2 Runtime."
            };
            ShowFailure(message);
        }
        catch (Exception) when (closed) { }
    }

    void ShowFailure(string message)
    {
        viewer.Visibility = Visibility.Collapsed;
        status.Text = message;
        status.Visibility = Visibility.Visible;
    }

    async Task<string> Perform(string action, string id, string value)
    {
        var file = Path.Combine(local, action is "refundLoad" or "refundStore" ? "refunds.json" : "state.json");
        switch (action)
        {
            case "device": return "ويندوز · تجربة محلية على هذه الحاسبة";
            case "refundLoad":
            case "load": return File.Exists(file) ? File.ReadAllText(file) : "";
            case "refundStore":
            case "store":
                if (Encoding.UTF8.GetByteCount(value) > 1_000_000) throw new InvalidDataException("سجل التجربة كبير.");
                var staging = file + ".new";
                using (var stream = new FileStream(staging,FileMode.Create,FileAccess.Write,FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(value));
                    stream.Flush(true);
                }
                File.Move(staging,file,true);
                return "";
            case "openRefund":
                if (openRefund == null || Encoding.UTF8.GetByteCount(value)>16384) throw new InvalidDataException("تعذّر فتح الدفعة.");
                await Dispatcher.InvokeAsync(()=>openRefund(this,value));
                return "";
            case "login":
                root = token = "";
                var app = PublicClientApplicationBuilder.Create(ClientId)
                    .WithAuthority("https://login.microsoftonline.com/consumers")
                    .WithRedirectUri("http://localhost").Build();
                var signed = await app.AcquireTokenInteractive(["https://graph.microsoft.com/Files.ReadWrite.AppFolder"])
                    .WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync(lifetime.Token);
                token = signed.AccessToken;
                return "";
            case "folder":
                using (var folder = JsonDocument.Parse(await Graph(Drive + "special/approot")))
                {
                    var item = folder.RootElement;
                    var folderId = item.GetProperty("id").GetString();
                    var driveId = item.GetProperty("parentReference").GetProperty("driveId").GetString();
                    if (string.IsNullOrEmpty(folderId) || string.IsNullOrEmpty(driveId)) throw new InvalidDataException("تعذّر تحديد مجلد التجربة.");
                    root = Drive + "items/" + Uri.EscapeDataString(folderId);
                    return driveId + "/" + folderId;
                }
        }
        if (root.Length == 0) throw new InvalidDataException("افتح مجلد التجربة أولاً.");
        if (action == "list")
        {
            string? next = root + "/children?$select=name&$top=200";
            var pages = new HashSet<string>();
            var names = new List<string>();
            while (next != null)
            {
                if (!pages.Add(next) || pages.Count > 100) throw new InvalidDataException("تعذرت قراءة فهرس التجربة.");
                using var page = JsonDocument.Parse(await Graph(next));
                var obj = page.RootElement;
                foreach (var item in obj.GetProperty("value").EnumerateArray())
                    if (item.GetProperty("name").GetString() is string name && (name.StartsWith(Prefix,StringComparison.Ordinal) || name.StartsWith(RefundPrefix,StringComparison.Ordinal))) names.Add(name);
                next = obj.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }
            return JsonSerializer.Serialize(names);
        }
        if (!Guid.TryParseExact(id,"D",out var key) || key.ToString("D") != id || action is not ("read" or "write" or "refundRead" or "refundWrite") || Encoding.UTF8.GetByteCount(value)>16384)
            throw new InvalidDataException("ملف تجربة غير صالح.");
        var prefix = action is "refundRead" or "refundWrite" ? RefundPrefix : Prefix;
        var url = root + ":/" + prefix + id + ".json:/content";
        if (action is "read" or "refundRead") return await Graph(url, download:true);
        await Graph(url + "?@microsoft.graph.conflictBehavior=fail", body:value);
        return "";
    }

    async Task<string> Graph(string url, string? body = null, bool download = false)
    {
        var uri = new Uri(url);
        if (token.Length == 0 || uri.Scheme != "https" || uri.Host != "graph.microsoft.com") throw new InvalidDataException("أعد تسجيل الدخول.");
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Put,uri);
            if (redirects == 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",token);
            if (body != null) request.Content = new StringContent(body,Encoding.UTF8,"application/json");
            using var response = await network.SendAsync(request,lifetime.Token);
            if (download && (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                uri = new Uri(uri,location);
                if (uri.Scheme != "https") break;
                continue;
            }
            if (body != null && response.StatusCode == HttpStatusCode.Conflict) return "";
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException("طلب OneDrive (" + (int)response.StatusCode + "). " +
                    (response.StatusCode == HttpStatusCode.Unauthorized ? "أعد تسجيل الدخول." : "تحقق من الاتصال والصلاحية ثم أعد المحاولة."));
            return await response.Content.ReadAsStringAsync(lifetime.Token);
        }
        throw new InvalidDataException("تعذّرت قراءة ملف التجربة.");
    }
}
