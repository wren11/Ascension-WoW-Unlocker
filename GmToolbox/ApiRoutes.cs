using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AscensionNetTool;

static class ApiRoutes
{
        public static void Map(WebApplication app, ToolSession s)
    {
        static string Rv(HttpRequest r, string key) => r.RouteValues[key]?.ToString() ?? "";
        static int RvInt(HttpRequest r, string key) => int.TryParse(Rv(r, key), out var v) ? v : 0;
        static long RvLong(HttpRequest r, string key) => long.TryParse(Rv(r, key), out var v) ? v : 0;
        static int? QInt(HttpRequest r, string key) =>
            int.TryParse(r.Query[key].FirstOrDefault(), out var v) ? v : null;
        static string? Q(HttpRequest r, string key)
        {
            var v = r.Query[key].FirstOrDefault();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        app.MapGet("/api/health", () => Results.Json(s.Health(), ToolSession.JsonOpts));
        app.MapGet("/api/status", () => Results.Json(s.BuildStatusDto(), ToolSession.JsonOpts));
        app.MapGet("/api/instances", () => Results.Json(s.BuildInstancesDto(), ToolSession.JsonOpts));
        app.MapGet("/api/settings", () => Results.Json(SettingsStore.Current, ToolSession.JsonOpts));
        app.MapGet("/api/setup/status", () => Results.Json(SettingsStore.SetupStatusDto(), ToolSession.JsonOpts));
        app.MapPost("/api/client/scan", async (HttpRequest req) =>
        {
            bool force = false;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                force = doc.RootElement.TryGetProperty("force", out var f) && f.GetBoolean();
            }
            catch { /* empty body */ }
            var r = force ? ClientOffsetService.ForceRescan(s.Log) : ClientOffsetService.EnsureFresh(s.Log);
            return Results.Json(r, ToolSession.JsonOpts);
        });
        app.MapPut("/api/settings", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<SettingsStore.Data>(req.Body, ToolSession.JsonOpts);
            if (body is null) return Results.BadRequest();
            string? pathErr = null;
            if (!string.IsNullOrWhiteSpace(body.AscensionExe))
            {
                if (!SettingsStore.TrySetAscensionExe(body.AscensionExe, out var e1))
                    pathErr = e1;
            }
            // Maps/mmtiles are optional — empty string clears them.
            if (!SettingsStore.TrySetMapsDir(body.MapsDir ?? "", out var e2))
                pathErr ??= e2;
            if (!SettingsStore.TrySetMmapsDir(body.MmapsDir ?? "", out var e3))
                pathErr ??= e3;
            SettingsStore.Current.InstanceCount = Math.Clamp(body.InstanceCount, 1, GmtLimits.MaxInstances);
            SettingsStore.Current.SharedSyncMs = body.SharedSyncMs;
            SettingsStore.Current.PollMs = body.PollMs;
            SettingsStore.Current.AutoSyncAddons = body.AutoSyncAddons;
            SettingsStore.Current.DarkMode = body.DarkMode;
            SettingsStore.Current.CorpusEnabled = body.CorpusEnabled;
            SettingsStore.Current.CorpusOnlineLearn = body.CorpusOnlineLearn;
            if (!string.IsNullOrWhiteSpace(body.TrainingDataDir))
                SettingsStore.Current.TrainingDataDir = body.TrainingDataDir.Trim();
            if (!string.IsNullOrWhiteSpace(body.CorpusLearnApi))
                SettingsStore.Current.CorpusLearnApi = body.CorpusLearnApi.Trim();
            SettingsStore.Current.NewcomersBotEnabled = body.NewcomersBotEnabled;
            if (!string.IsNullOrWhiteSpace(body.NewcomersBotChannel))
                SettingsStore.Current.NewcomersBotChannel = body.NewcomersBotChannel.Trim();
            if (!string.IsNullOrWhiteSpace(body.NewcomersBotChatApi))
                SettingsStore.Current.NewcomersBotChatApi = body.NewcomersBotChatApi.Trim();
            SettingsStore.Current.NewcomersBotMinIntervalMs = Math.Clamp(body.NewcomersBotMinIntervalMs, 2000, 120_000);
            SettingsStore.Current.NewcomersBotMaxTokens = Math.Clamp(body.NewcomersBotMaxTokens, 24, 96);
            SettingsStore.Current.NewcomersBotReplyToAll = body.NewcomersBotReplyToAll;
            SettingsStore.Current.NewcomersBotTagPrefix = body.NewcomersBotTagPrefix;
            if (!string.IsNullOrWhiteSpace(body.NewcomersBotPersona))
                SettingsStore.Current.NewcomersBotPersona = body.NewcomersBotPersona.Trim();
            if (body.LicensePath is not null)
                SettingsStore.Current.LicensePath = body.LicensePath.Trim();
            if (body.LicensePassword is not null)
                SettingsStore.Current.LicensePassword = body.LicensePassword;
            SettingsStore.Current.SoftRealmUrl = "";
            SettingsStore.Save();
            Paths.ApplySettings(SettingsStore.Current);
            object? scan = null;
            if (SettingsStore.IsAscensionConfigured())
                scan = ClientOffsetService.ForceRescan(s.Log);
            var license = LocalAccess.StatusDto();
            return Results.Json(new
            {
                settings = SettingsStore.Current,
                setup = SettingsStore.SetupStatusDto(),
                pathError = pathErr,
                scan,
                license,
            }, ToolSession.JsonOpts);
        });

        app.MapGet("/api/license", () =>
            Results.Json(LocalAccess.StatusDto(), ToolSession.JsonOpts));
        app.MapPost("/api/license/verify", () =>
            Results.Json(LocalAccess.StatusDto(), ToolSession.JsonOpts));

        app.MapGet("/api/account", () =>
            Results.Json(LocalAccess.StatusDto(), ToolSession.JsonOpts));
        app.MapPost("/api/account/login", () =>
            Results.Json(new { ok = true, already = true, license = LocalAccess.StatusDto() }, ToolSession.JsonOpts));
        app.MapPost("/api/account/logout", () =>
            Results.Json(new { ok = true, license = LocalAccess.StatusDto() }, ToolSession.JsonOpts));
        app.MapPost("/api/account/sync-character", async (HttpRequest req) =>
        {
            string? guid = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("guid", out var g))
                    guid = g.GetString();
            }
            catch { /* empty */ }
            return Results.Json(await s.SyncCharacterEntitlementsAsync(guid), ToolSession.JsonOpts);
        });

        app.MapPost("/api/instances/launch", async (HttpRequest req) =>
        {
            if (SettingsStore.NeedsSetup())
                return Results.Json(new { ok = false, error = SettingsStore.DescribeMissing() }, ToolSession.JsonOpts);
            using var doc = await JsonDocument.ParseAsync(req.Body);
            int count = doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : SettingsStore.Current.InstanceCount;
            return Results.Json(await s.LaunchAsync(count), ToolSession.JsonOpts);
        });
        app.MapPost("/api/instances/select", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            bool shared = doc.RootElement.TryGetProperty("shared", out var sh) && sh.GetBoolean();
            int? id = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt32() : null;
            s.SelectInstance(id, shared);
            return Results.Ok();
        });
        app.MapPost("/api/instances/add", async () =>
        {
            if (SettingsStore.NeedsSetup())
                return Results.Json(new { ok = false, error = SettingsStore.DescribeMissing() }, ToolSession.JsonOpts);
            int n = s.Instances.All.Count() + 1;
            return Results.Json(await s.LaunchAsync(n), ToolSession.JsonOpts);
        });

        app.MapPost("/api/deploy/all", () =>
        {
            return Results.Json(s.DeployAll(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/deploy/proxy", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            bool force = doc.RootElement.TryGetProperty("force", out var f) && f.GetBoolean();
            return Results.Json(s.DeployProxy(force), ToolSession.JsonOpts);
        });
        app.MapPost("/api/deploy/addons", () =>
        {
            return Results.Json(s.DeployAddons(), ToolSession.JsonOpts);
        });

        app.MapGet("/api/addons", () =>
        {
            return Results.Json(s.RefreshAddons(false), ToolSession.JsonOpts);
        });
        app.MapPost("/api/addons/scan", () =>
        {
            return Results.Json(s.RefreshAddons(true), ToolSession.JsonOpts);
        });
        app.MapPost("/api/addons/{name}/install", async (HttpRequest req) =>
        {
            var name = Rv(req, "name");
            try
            {
                var liveInst = s.Instances.All.FirstOrDefault()
                    ?? s.Instances.All.FirstOrDefault(i => i.PlayerGuid != 0);
                s.Addons.DeployOne(name, liveInst?.Id ?? 0);
                return Results.Json(new { ok = true }, ToolSession.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, ToolSession.JsonOpts);
            }
        });

        app.MapPost("/api/proxy/sniff", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            bool on = doc.RootElement.TryGetProperty("on", out var o) && o.GetBoolean();
            s.SetSniff(on);
            return Results.Ok();
        });

        app.MapGet("/api/opcodes/ignore", () => Results.Json(s.GetOpcodeFilter(), ToolSession.JsonOpts));
        app.MapPut("/api/opcodes/ignore", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            bool reset = doc.RootElement.TryGetProperty("resetDefaults", out var rd) && rd.GetBoolean();
            bool? chatCap = doc.RootElement.TryGetProperty("chatCapture", out var cc) && cc.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? cc.GetBoolean() : null;
            List<uint>? ops = null;
            if (doc.RootElement.TryGetProperty("opcodes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                ops = new List<uint>();
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number) ops.Add(el.GetUInt32());
                    else if (el.ValueKind == JsonValueKind.String
                        && uint.TryParse(el.GetString()?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                            System.Globalization.NumberStyles.HexNumber, null, out var hv))
                        ops.Add(hv);
                }
            }
            return Results.Json(s.SetOpcodeFilter(ops, chatCap, reset), ToolSession.JsonOpts);
        });
        app.MapPost("/api/opcodes/ignore/{opcode:int}", (HttpRequest req) =>
            Results.Json(s.AddOpcodeIgnore((uint)RvInt(req, "opcode")), ToolSession.JsonOpts));
        app.MapDelete("/api/opcodes/ignore/{opcode:int}", (HttpRequest req) =>
            Results.Json(s.RemoveOpcodeIgnore((uint)RvInt(req, "opcode")), ToolSession.JsonOpts));

        app.MapGet("/api/chat/stats", () => Results.Json(s.ChatLog.Stats(), ToolSession.JsonOpts));
        app.MapGet("/api/chat/messages", (HttpRequest req) =>
            Results.Json(s.ChatLog.RecentMessages(QInt(req, "limit") ?? 200, Q(req, "channel")), ToolSession.JsonOpts));
        app.MapGet("/api/chat/channels", () => Results.Json(s.ChatLog.Channels(), ToolSession.JsonOpts));
        app.MapGet("/api/chat/users", () => Results.Json(s.ChatLog.Users(), ToolSession.JsonOpts));
        app.MapGet("/api/chat/players", (HttpRequest req) =>
            Results.Json(s.ChatLog.Players(QInt(req, "limit") ?? 200), ToolSession.JsonOpts));
        app.MapGet("/api/chat/packet-strings", (HttpRequest req) =>
            Results.Json(s.ChatLog.RecentPacketStrings(QInt(req, "limit") ?? 50), ToolSession.JsonOpts));
        app.MapPost("/api/chat/say", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            string text = doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            // Manual chat: trim + clamp only — do NOT apply [AI] bot prefix.
            text = Regex.Replace((text ?? "").Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
            if (text.Length > 255) text = text[..255];
            if (text.Length < 1) return Results.BadRequest(new { error = "empty" });
            string channel = doc.RootElement.TryGetProperty("channel", out var ch)
                ? (ch.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(channel))
                channel = "SAY";
            var r = s.ChatSay(channel, text);
            return Results.Json(r, ToolSession.JsonOpts);
        });
        app.MapGet("/api/corpus/stats", () => Results.Json(new { enabled = false }, ToolSession.JsonOpts));
        app.MapPost("/api/corpus/reload", () => Results.Json(new { enabled = false }, ToolSession.JsonOpts));
        app.MapGet("/api/newcomers-bot/stats", () => Results.Json(new { enabled = false }, ToolSession.JsonOpts));
        app.MapPost("/api/newcomers-bot/enable", () =>
            Results.Json(new { ok = false, enabled = false, error = "removed" }, ToolSession.JsonOpts));
        app.MapPost("/api/newcomers-bot/say", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            string text = doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            text = Regex.Replace((text ?? "").Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
            if (text.Length < 1) return Results.BadRequest(new { error = "empty" });
            var r = s.ChatSay("SAY", text);
            return Results.Json(r, ToolSession.JsonOpts);
        });
        app.MapPost("/api/proxy/lua", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            string script = doc.RootElement.GetProperty("script").GetString() ?? "";
            return Results.Json(s.RunLua(script), ToolSession.JsonOpts);
        });
        app.MapPost("/api/proxy/hacks", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            uint bits = doc.RootElement.TryGetProperty("bits", out var b) ? b.GetUInt32() : 0u;
            bool fly = doc.RootElement.TryGetProperty("fly", out var f) && f.ValueKind == JsonValueKind.True;
            return Results.Json(s.SetHacks(bits, fly), ToolSession.JsonOpts);
        });
        app.MapPost("/api/proxy/nameplate-range", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            double yards = doc.RootElement.TryGetProperty("yards", out var y) ? y.GetDouble() : 41.0;
            string script =
                $"if type(GmSetNamePlateRange)=='function' then GmSetNamePlateRange({yards:0.##}) end "
                + $"if type(SetNamePlateRange)=='function' then pcall(SetNamePlateRange,{yards:0.##}) end "
                + $"if type(SetCVar)=='function' then pcall(SetCVar,'nameplateDistance',{yards:0.##}) end "
                + $"print('[GmTB] nameplate',{yards:0.##})";
            return Results.Json(s.RunLua(script), ToolSession.JsonOpts);
        });
        app.MapPost("/api/proxy/cheat", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            string name = doc.RootElement.TryGetProperty("opcode", out var o) ? o.GetString() ?? "" : "";
            string hex = doc.RootElement.TryGetProperty("bodyHex", out var h) ? h.GetString() ?? "" : "";
            byte[] body = hex.Length > 0 ? Convert.FromHexString(hex) : Array.Empty<byte>();
            return Results.Json(s.ReplayGmCheat(name, body), ToolSession.JsonOpts);
        });
        app.MapPost("/api/proxy/replay", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return Results.Json(s.Replay(doc.RootElement.GetProperty("hex").GetString() ?? ""), ToolSession.JsonOpts);
        });
        app.MapPost("/api/proxy/inject", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return Results.Json(s.Inject(doc.RootElement.GetProperty("hex").GetString() ?? ""), ToolSession.JsonOpts);
        });

        app.MapGet("/api/packets", (HttpRequest req) =>
            Results.Json(s.GetPackets(Q(req, "dir"), QInt(req, "opcode")), ToolSession.JsonOpts));
        app.MapGet("/api/packets/{seq:long}", (HttpRequest req) =>
        {
            var seq = RvLong(req, "seq");
            int? inst = QInt(req, "inst");
            if (seq < 0 || seq > uint.MaxValue)
                return Results.NotFound(new { error = "packet not found" });
            var pkt = s.GetPacket((uint)seq, inst);
            return pkt is null
                ? Results.NotFound(new { error = "packet not found" })
                : Results.Json(pkt, ToolSession.JsonOpts);
        });
        app.MapPost("/api/gui/ping", () => Results.Json(s.TouchGui(), ToolSession.JsonOpts));
        app.MapPost("/api/packets/clear", () => { s.ClearPackets(); return Results.Ok(); });
        app.MapPost("/api/packets/describe", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return Results.Json(s.DescribePacket(doc.RootElement.GetProperty("hex").GetString() ?? ""), ToolSession.JsonOpts);
        });

        app.MapGet("/api/objects", () => Results.Json(s.GetObjectsDto(), ToolSession.JsonOpts));
        app.MapGet("/api/opcodes", () =>
            Results.Json(Opcodes.All.Select(x => new { opcode = x.Op, name = x.Name }).ToList(), ToolSession.JsonOpts));

        app.MapGet("/api/bookmarks", () =>
            Results.Json(Enumerable.Range(1, PacketBookmarkStore.SlotCount).Select(i =>
            {
                var b = PacketBookmarkStore.Get(i);
                return new { slot = i, hex = b?.Hex ?? "", label = b?.Label ?? "", dir = b?.Dir ?? 0 };
            }).ToList(), ToolSession.JsonOpts));
        app.MapPut("/api/bookmarks/{slot:int}", async (HttpRequest req) =>
        {
            int slot = RvInt(req, "slot");
            using var doc = await JsonDocument.ParseAsync(req.Body);
            string hex = doc.RootElement.TryGetProperty("hex", out var h) ? h.GetString() ?? "" : "";
            string label = doc.RootElement.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            int dir = doc.RootElement.TryGetProperty("dir", out var d) ? d.GetInt32() : 0;
            var bytes = PacketView.ParseHex(hex);
            if (bytes is null || bytes.Length == 0)
                return Results.BadRequest(new { error = "bad hex" });
            PacketBookmarkStore.Set(slot, bytes, dir, label);
            return Results.Ok();
        });
        app.MapDelete("/api/bookmarks/{slot:int}", (HttpRequest req) =>
        {
            PacketBookmarkStore.Clear(RvInt(req, "slot"));
            return Results.Ok();
        });
        app.MapPost("/api/bookmarks/sync", () =>
        {
            return Results.Json(s.BookmarkSync(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/bookmarks/{slot:int}/fire", (HttpRequest req) =>
        {
            return Results.Json(s.BookmarkFire(RvInt(req, "slot")), ToolSession.JsonOpts);
        });
        app.MapPost("/api/bookmarks/loop", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            bool on = doc.RootElement.TryGetProperty("on", out var o) && o.GetBoolean();
            return Results.Json(s.BookmarkLoop(on), ToolSession.JsonOpts);
        });
        app.MapPost("/api/bookmarks/burst", () =>
        {
            return Results.Json(s.BookmarkBurst(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/bookmarks/{slot:int}/export-bot", (HttpRequest req) =>
            Results.Json(s.ExportBookmarkForBot(RvInt(req, "slot")), ToolSession.JsonOpts));

        app.MapGet("/api/watchdog", () => Results.Json(s.Watchdog?.StatusDto() ?? new { enabled = false }, ToolSession.JsonOpts));
        app.MapPut("/api/watchdog", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var cur = SettingsStore.Current;
            if (doc.RootElement.TryGetProperty("enabled", out var e)) cur.WatchdogEnabled = e.GetBoolean();
            if (doc.RootElement.TryGetProperty("autoRelaunch", out var ar)) cur.WatchdogAutoRelaunch = ar.GetBoolean();
            if (doc.RootElement.TryGetProperty("autoRelog", out var al)) cur.WatchdogAutoRelog = al.GetBoolean();
            if (doc.RootElement.TryGetProperty("restoreBots", out var rb)) cur.WatchdogRestoreBots = rb.GetBoolean();
            if (doc.RootElement.TryGetProperty("account", out var ac)) cur.WatchdogAccount = ac.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("password", out var pw)) cur.WatchdogPassword = pw.GetString() ?? "";
            SettingsStore.Save();
            return Results.Json(s.Watchdog?.StatusDto() ?? new { ok = true }, ToolSession.JsonOpts);
        });
        app.MapPost("/api/watchdog/recover/{id:int}", async (HttpRequest req) =>
        {
            if (s.Watchdog is null) return Results.Json(new { ok = false, error = "no watchdog" });
            return Results.Json(await s.Watchdog.RecoverNowAsync(RvInt(req, "id")), ToolSession.JsonOpts);
        });

        app.MapGet("/api/schemas", () =>
            Results.Json(PacketSchemaRegistry.All.Select(x => new
            {
                x.Opcode, x.Name, x.Dir, x.Source, fields = x.Fields
            }).ToList(), ToolSession.JsonOpts));
        app.MapPost("/api/schemas/infer", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            string hex = doc.RootElement.GetProperty("hex").GetString() ?? "";
            var bytes = PacketView.ParseHex(hex);
            if (bytes is null) return Results.BadRequest(new { error = "bad hex" });
            uint op = BitConverter.ToUInt32(bytes, 0);
            if (op > 0xFFFF) op = BitConverter.ToUInt16(bytes, 0);
            var schema = PacketSchemaRegistry.InferAndStore(Opcodes.Normalize(op), bytes);
            PacketSchemaRegistry.SaveDisk();
            return Results.Json(schema, ToolSession.JsonOpts);
        });

        app.MapGet("/api/fuzz/status", () =>
        {
            var f = s.Fuzz;
            if (f is null) return Results.Json(new { phase = "none" });
            return Results.Json(new
            {
                f.Phase, running = f.IsRunning, paused = f.IsPaused,
                f.Sent, hits = f.Interesting, f.Crashes, f.NoResponse,
                blacklisted = f.Blacklisted, opcode = f.Opcode, seedLen = f.Seed.Length,
                seedHex = f.Seed.Length > 0 ? Convert.ToHexString(f.Seed) : "",
                message = f.LastMessage,
            }, ToolSession.JsonOpts);
        });
        app.MapPost("/api/fuzz/seed", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            string hex = root.TryGetProperty("hex", out var hx) ? hx.GetString() ?? "" : "";
            uint? opcode = null;
            if (root.TryGetProperty("opcode", out var o) && o.ValueKind is JsonValueKind.Number)
                opcode = (uint)o.GetInt32();
            uint? seq = null;
            if (root.TryGetProperty("seq", out var sq) && sq.ValueKind is JsonValueKind.Number)
                seq = (uint)sq.GetInt64();
            int? inst = null;
            if (root.TryGetProperty("inst", out var ins) && ins.ValueKind is JsonValueKind.Number)
                inst = ins.GetInt32();
            var result = s.LoadFuzzSeed(hex, opcode, seq, inst);
            return Results.Json(result, ToolSession.JsonOpts);
        });
        app.MapPost("/api/fuzz/start", () =>
        {
            try { s.Fuzz?.Start(); return Results.Json(new { ok = true, phase = s.Fuzz?.Phase }); }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
        });
        app.MapPost("/api/fuzz/stop", () => { s.Fuzz?.Stop(); return Results.Json(new { ok = true }); });
        app.MapPost("/api/fuzz/pause", () => { s.Fuzz?.Pause(); return Results.Json(new { ok = true }); });
        app.MapPost("/api/fuzz/resume", () => { s.Fuzz?.Resume(); return Results.Json(new { ok = true }); });

        // Character Create lab (owned Ascension client — unlock + bot + login experiments)
        app.MapGet("/api/charcreate/status", () => Results.Json(s.CharCreateStatus(), ToolSession.JsonOpts));
        app.MapPost("/api/charcreate/unlock", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            bool on = !doc.RootElement.TryGetProperty("on", out var o) || o.GetBoolean();
            return Results.Json(s.CharCreateUnlock(on), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/probe", () =>
        {
            return Results.Json(s.CharCreateProbe(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/random-name", () =>
        {
            return Results.Json(s.CharCreateRandomName(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/experiments", () =>
        {
            return Results.Json(s.CharCreateExperiments(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/chaos", () =>
        {
            return Results.Json(s.CharCreateChaos(), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/force", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var e = doc.RootElement;
            int race = e.TryGetProperty("race", out var r) ? r.GetInt32() : 1;
            int classId = e.TryGetProperty("classId", out var c) ? c.GetInt32() : 1;
            int sex = e.TryGetProperty("sex", out var sx) ? sx.GetInt32() : 0;
            int skin = e.TryGetProperty("skin", out var sk) ? sk.GetInt32() : 0;
            int face = e.TryGetProperty("face", out var f) ? f.GetInt32() : 0;
            int hairStyle = e.TryGetProperty("hairStyle", out var hs) ? hs.GetInt32() : 0;
            int hairColor = e.TryGetProperty("hairColor", out var hc) ? hc.GetInt32() : 0;
            int facial = e.TryGetProperty("facial", out var fa) ? fa.GetInt32() : 0;
            return Results.Json(s.CharCreateForceAppear(race, classId, sex, skin, face, hairStyle, hairColor, facial), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/create", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var e = doc.RootElement;
            string? name = e.TryGetProperty("name", out var n) ? n.GetString() : null;
            int race = e.TryGetProperty("race", out var r) ? r.GetInt32() : 1;
            int classId = e.TryGetProperty("classId", out var c) ? c.GetInt32() : 1;
            int sex = e.TryGetProperty("sex", out var sx) ? sx.GetInt32() : 0;
            bool randomize = !e.TryGetProperty("randomize", out var rz) || rz.GetBoolean();
            bool weird = e.TryGetProperty("weird", out var w) && w.GetBoolean();
            bool unlock = !e.TryGetProperty("unlock", out var u) || u.GetBoolean();
            return Results.Json(s.CharCreateOne(name, race, classId, sex, randomize, weird, unlock), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/bot/start", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var e = doc.RootElement;
            int count = e.TryGetProperty("count", out var n) ? n.GetInt32() : 5;
            int delayMs = e.TryGetProperty("delayMs", out var d) ? d.GetInt32() : 2500;
            bool weird = e.TryGetProperty("weird", out var w) && w.GetBoolean();
            bool unlock = !e.TryGetProperty("unlock", out var u) || u.GetBoolean();
            int race = e.TryGetProperty("race", out var r) ? r.GetInt32() : 0;
            int classId = e.TryGetProperty("classId", out var c) ? c.GetInt32() : 0;
            int sex = e.TryGetProperty("sex", out var sx) ? sx.GetInt32() : 0;
            return Results.Json(s.CharCreateBotStart(count, delayMs, weird, unlock, race, classId, sex), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/bot/stop", () => Results.Json(s.CharCreateBotStop(), ToolSession.JsonOpts));
        app.MapPost("/api/charcreate/select", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            int index = doc.RootElement.TryGetProperty("index", out var i) ? i.GetInt32() : 1;
            return Results.Json(s.CharCreateSelect(index), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/enter", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            int index = doc.RootElement.TryGetProperty("index", out var i) ? i.GetInt32() : 1;
            return Results.Json(s.CharCreateEnterWorld(index), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/replay-login", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var e = doc.RootElement;
            string? guid = e.TryGetProperty("guid", out var g) ? g.GetString() : null;
            string? hex = e.TryGetProperty("hex", out var h) ? h.GetString() : null;
            return Results.Json(s.CharCreateReplayLogin(guid, hex), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/replay-enum", async (HttpRequest req) =>
        {
            string? hex = null;
            if (req.ContentLength is > 0)
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                hex = doc.RootElement.TryGetProperty("hex", out var h) ? h.GetString() : null;
            }
            return Results.Json(s.CharCreateReplayEnum(hex), ToolSession.JsonOpts);
        });
        app.MapPost("/api/charcreate/multi-login", async (HttpRequest req) =>
        {
            string? guid = null;
            if (req.ContentLength is > 0)
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                guid = doc.RootElement.TryGetProperty("guid", out var g) ? g.GetString() : null;
            }
            return Results.Json(s.CharCreateMultiLoginExperiment(guid), ToolSession.JsonOpts);
        });

        app.MapGet("/api/logs", () => Results.Text(s.GetLog(), "text/plain"));
        app.MapGet("/api/audio", () => Results.Json(new
        {
            enabled = false,
            muted = true,
            volume = 0,
            status = "",
        }, ToolSession.JsonOpts));
        app.MapPost("/api/audio/toggle", () =>
            Results.Json(new { status = "" }, ToolSession.JsonOpts));
        app.MapGet("/api/discord", () => Results.Json(new
        {
            url = HealthCheckService.DiscordInviteUrl,
            label = HealthCheckService.DiscordInviteLabel,
        }, ToolSession.JsonOpts));

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await s.AcceptWebSocketAsync(ws, context.RequestAborted);
        });
    }
}
