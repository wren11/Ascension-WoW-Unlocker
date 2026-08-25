using System.Net;
using Photino.NET;

namespace AscensionNetTool;

static class Program
{
    public static ToolSession Session { get; private set; } = null!;

    [STAThread]
    static void Main(string[] args)
    {
        SettingsStore.Load();
        Paths.ApplySettings(SettingsStore.Current);
        Session = new ToolSession();
        Session.Start();

        int port = FindFreePort();
        var webRoot = ResolveWebRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = Paths.ContentRoot,
            WebRootPath = webRoot,
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseExceptionHandler(err =>
        {
            err.Run(async ctx =>
            {
                var feat = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                var ex = feat?.Error;
                try
                {
                    if (ex != null)
                        File.AppendAllText(Paths.ToolLogPath, $"[{DateTime.Now:HH:mm:ss}] UI 500 {ctx.Request.Path}: {ex}\n");
                }
                catch { /* ignore */ }
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("GMToolBox UI error: " + (ex?.Message ?? "unknown"));
            });
        });
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                try { File.AppendAllText(Paths.ToolLogPath, $"[{DateTime.Now:HH:mm:ss}] UI 500 {ctx.Request.Path}: {ex}\n"); }
                catch { /* ignore */ }
                if (ctx.Response.HasStarted) throw;
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("GMToolBox UI error: " + ex.Message);
            }
        });
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        ApiRoutes.Map(app, Session);
        if (File.Exists(Path.Combine(webRoot, "index.html")))
            app.MapFallbackToFile("index.html");
        else
            app.MapGet("/", () => Results.Text("GMToolBox UI files are missing (wwwroot/index.html).", "text/plain"));

        _ = app.StartAsync();
        try
        {
            var n = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.Count;
            File.AppendAllText(Paths.ToolLogPath,
                $"[{DateTime.Now:HH:mm:ss}] UI http://127.0.0.1:{port}/ endpoints={n} webroot={webRoot}\n");
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Paths.ToolLogPath,
                    $"[{DateTime.Now:HH:mm:ss}] UI endpoint build failed: {ex}\n");
            }
            catch { /* ignore */ }
        }

        string url = $"http://127.0.0.1:{port}/";
        var window = new PhotinoWindow()
            .SetTitle("GMToolBox")
            .SetUseOsDefaultSize(false)
            .SetSize(1480, 940)
            .SetMinSize(1100, 720)
            .Center()
            .SetResizable(true)
            .SetLogVerbosity(0)
            .Load(new Uri(url));

        window.WaitForClose();
        Session.Dispose();
        app.StopAsync().GetAwaiter().GetResult();
    }

    static string ResolveWebRoot()
    {
        foreach (var root in new[] { Paths.ContentRoot, Paths.ExeDir, Paths.AppRoot })
        {
            var wr = Path.Combine(root, "wwwroot");
            if (Paths.IsGmToolBoxWebRoot(wr))
                return wr;
        }

        var dest = Path.Combine(Paths.ExeDir, "wwwroot");
        foreach (var srcRoot in new[] { Paths.ContentRoot, Paths.ExeDir, Paths.AppRoot })
        {
            var src = Path.Combine(srcRoot, "wwwroot");
            if (!Directory.Exists(src) || src.Equals(dest, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Paths.IsGmToolBoxWebRoot(src))
                continue;
            try
            {
                CopyTree(src, dest);
                if (Paths.IsGmToolBoxWebRoot(dest))
                    return dest;
            }
            catch { /* try next */ }
        }

        Directory.CreateDirectory(dest);
        return dest;
    }

    static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = file.Substring(src.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
            var outPath = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.Copy(file, outPath, overwrite: true);
        }
    }

    static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
