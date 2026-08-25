using System.Collections.Concurrent;
using System.Buffers.Binary;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Monitoring;

namespace HeadlessClient.Infrastructure.Probe;

/// <summary>
/// GM security audit: fire high-risk economy/cheat/quest CMSG and flag any that
/// elicit grant-class SMSG (XP, gold, items, quest complete, achievements).
/// Purpose: find server gaps that players could abuse — not to farm.
/// </summary>
public sealed class EconomySecurityAudit
{
    public sealed record Finding(
        DateTimeOffset At,
        uint Cmsg,
        string CmsgName,
        string RiskFamily,
        string Variant,
        string PayloadHex,
        string Verdict,
        string Summary,
        IReadOnlyList<string> GrantSmsg,
        IReadOnlyList<ProbeSmsg> Responses);

    private readonly ConcurrentQueue<Finding> _findings = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly ConcurrentDictionary<uint, byte> _tested = new();
    private readonly object _gate = new();
    private readonly ProbeDataPool _pool = new();

    private IWorldClient? _world;
    private InMemoryObjectDirectory? _objects;
    private string _character = "";
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _armed;
    private int _sent;
    private int _gaps;
    private string _phase = "idle";
    private string _lastError = "";

    /// <summary>SMSG that indicate the server granted economy value.</summary>
    private static readonly Dictionary<uint, string> GrantOpcodes = new()
    {
        [0x01D0] = "SMSG_LOG_XPGAIN",
        [0x01F8] = "SMSG_EXPLORATION_EXPERIENCE",
        [0x0163] = "SMSG_LOOT_MONEY_NOTIFY",
        [0x0164] = "SMSG_LOOT_ITEM_NOTIFY",
        [0x0165] = "SMSG_LOOT_CLEAR_MONEY",
        [0x0166] = "SMSG_ITEM_PUSH_RESULT",
        [0x0160] = "SMSG_LOOT_RESPONSE",
        [0x018D] = "SMSG_QUESTGIVER_OFFER_REWARD",
        [0x0191] = "SMSG_QUESTGIVER_QUEST_COMPLETE",
        [0x0198] = "SMSG_QUESTUPDATE_COMPLETE",
        [0x01A4] = "SMSG_BUY_ITEM",
        [0x01A1] = "SMSG_SELL_ITEM",
        [0x0468] = "SMSG_ACHIEVEMENT_EARNED",
        [0x0239] = "SMSG_SEND_MAIL_RESULT",
        [0x0285] = "SMSG_RECEIVED_MAIL",
        [0x0120] = "SMSG_TRADE_STATUS",
        [0x09A1] = "SMSG_PATCH_QUEST_XP",
    };

    private static readonly HashSet<uint> NoiseSmsg = new()
    {
        0x00A9, 0x01F6, 0x0578, 0x00AA, 0x00DD,
        0x047F, 0x0480, 0x0495, 0x0496, 0x0673, 0x0674,
        0x0390, 0x01DD, 0x0096, 0x0099, 0x077C,
    };

    /// <summary>Never fire these even in audit mode.</summary>
    private static readonly HashSet<string> NeverFire = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMSG_CHAR_DELETE", "CMSG_CHAR_CREATE", "CMSG_REBOOT_ME", "CMSG_BOOTME",
        "CMSG_LOGOUT_REQUEST", "CMSG_LOGOUT_CANCEL", "CMSG_PLAYER_LOGOUT",
        "CMSG_AUTH_SESSION", "CMSG_AUTH_SRP6_BEGIN", "CMSG_AUTH_SRP6_PROOF",
        "CMSG_GM_NUKE", "CMSG_GM_NUKE_ACCOUNT", "CMSG_GM_NUKE_CHARACTER",
        "CMSG_GM_DESTROY_ONLINE_CORPSE", "CMSG_MESSAGECHAT",
    };

    public bool IsArmed => Volatile.Read(ref _armed) != 0;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public string Phase => _phase;
    public int Sent => Volatile.Read(ref _sent);
    public int GapCount => Volatile.Read(ref _gaps);
    public string LastError => _lastError;
    public string Character => _character;
    public ProbeDataPool Pool => _pool;

    public void Arm(bool on)
    {
        Interlocked.Exchange(ref _armed, on ? 1 : 0);
        Log(on
            ? "AUDIT ARMED — will fire high-risk CMSG to find economy gaps (GM security test)"
            : "audit disarmed");
    }

    public void Attach(IWorldClient world, InMemoryObjectDirectory objects, string character, ulong selfGuid)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_gate)
        {
            _world = world;
            _objects = objects;
            _character = character ?? "";
            if (selfGuid != 0)
            {
                _pool.SetSelf(selfGuid, 0, 0, 0, 0);
            }
        }

        RefreshPool();
        Log($"audit attached as {_character}");
    }

    public void Detach(IWorldClient world)
    {
        Stop();
        lock (_gate)
        {
            if (_world == world)
            {
                _world = null;
            }
        }
    }

    public void RefreshPool()
    {
        var objs = _objects?.Snapshot() ?? Array.Empty<WorldObject>();
        _pool.IngestObjects(objs);
    }

    public void Stop()
    {
        try { _runCts?.Cancel(); } catch { /* ignore */ }
        _phase = "idle";
    }

    public object GetStatus() => new
    {
        ok = true,
        mode = "economy-security-audit",
        armed = IsArmed,
        running = IsRunning,
        phase = _phase,
        character = _character,
        sent = Sent,
        gaps = GapCount,
        tested = _tested.Count,
        catalogRisk = BuildRiskCatalog(includeGm: true).Count,
        lastError = _lastError,
        purpose = "Find server gaps where cheat/economy CMSG grant XP/gold/items/quest rewards"
    };

    public IReadOnlyList<Finding> RecentFindings(int max = 300) =>
        _findings.Reverse().Take(max).ToList();

    public IReadOnlyList<string> RecentLog(int max = 200) =>
        _log.Reverse().Take(max).Reverse().ToList();

    public IReadOnlyList<object> GapSummary() =>
        _findings
            .Where(f => f.Verdict is "GAP_CRITICAL" or "GAP_WARN")
            .GroupBy(f => f.CmsgName)
            .Select(g => (object)new
            {
                cmsg = g.First().Cmsg,
                name = g.Key,
                family = g.First().RiskFamily,
                verdict = g.MaxBy(x => x.Verdict == "GAP_CRITICAL")!.Verdict,
                count = g.Count(),
                sample = g.First().Summary
            })
            .ToList();

    public void StartAudit(bool includeGm, bool includeLegitEconomy, int maxOpcodes, int delayMs)
    {
        if (!IsArmed)
        {
            throw new InvalidOperationException("Arm the security audit first (GM gap test).");
        }

        if (IsRunning)
        {
            throw new InvalidOperationException("Audit already running.");
        }

        RefreshPool();
        _runCts = new CancellationTokenSource();
        _phase = "audit-running";
        _lastError = "";
        _runTask = Task.Run(() => RunAsync(includeGm, includeLegitEconomy, maxOpcodes, delayMs, _runCts.Token));
    }

    public static IReadOnlyList<(uint Opcode, string Name, string Family)> BuildRiskCatalog(bool includeGm)
    {
        var list = new List<(uint, string, string)>();
        foreach (var (op, name) in WotlkCmsgCatalog.All)
        {
            if (NeverFire.Contains(name))
            {
                continue;
            }

            if (name.StartsWith("CMSG_GM_", StringComparison.OrdinalIgnoreCase) && !includeGm)
            {
                continue;
            }

            var family = ClassifyFamily(name);
            if (family is null)
            {
                continue;
            }

            list.Add((op, name, family));
        }

        return list
            .OrderBy(x => FamilyPriority(x.Item3))
            .ThenBy(x => x.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task RunAsync(
        bool includeGm,
        bool includeLegitEconomy,
        int maxOpcodes,
        int delayMs,
        CancellationToken ct)
    {
        try
        {
            var catalog = BuildRiskCatalog(includeGm)
                .Where(x => includeLegitEconomy || x.Family is not ("loot" or "quest_legit" or "vendor" or "mail" or "trade"))
                .Where(x => !_tested.ContainsKey(x.Opcode))
                .Take(Math.Clamp(maxOpcodes, 1, 500))
                .ToList();

            Log($"audit start targets={catalog.Count} includeGm={includeGm} legitEconomy={includeLegitEconomy} delay={delayMs}ms");

            foreach (var (opcode, name, family) in catalog)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsArmed)
                {
                    Log("audit disarmed mid-run — stop");
                    break;
                }

                var variants = BuildAuditVariants(opcode, name);
                foreach (var v in variants)
                {
                    ct.ThrowIfCancellationRequested();
                    await FireAsync(opcode, name, family, v, ct).ConfigureAwait(false);
                    await Task.Delay(Math.Max(80, delayMs), ct).ConfigureAwait(false);
                }

                _tested[opcode] = 1;
            }

            _phase = "done";
            Log($"audit complete sent={Sent} gaps={GapCount} tested={_tested.Count}");
        }
        catch (OperationCanceledException)
        {
            _phase = "stopped";
            Log("audit cancelled");
        }
        catch (Exception ex)
        {
            _phase = "error";
            _lastError = ex.Message;
            Log("audit error: " + ex.Message);
        }
    }

    private async Task FireAsync(
        uint opcode,
        string name,
        string family,
        ProbeVariant variant,
        CancellationToken ct)
    {
        var world = RequireWorld();
        var before = DateTimeOffset.UtcNow;
        var inbox = new ConcurrentQueue<Packet>();
        void Grab(Packet p) => inbox.Enqueue(p);
        world.PacketReceived += Grab;
        try
        {
            await world.SendAsync(new Packet(opcode, variant.Payload), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        finally
        {
            world.PacketReceived -= Grab;
        }

        var responses = new List<ProbeSmsg>();
        while (inbox.TryDequeue(out var pkt))
        {
            var offset = (long)(DateTimeOffset.UtcNow - before).TotalMilliseconds;
            responses.Add(ProbePacketFormatter.Build(
                pkt.Opcode,
                WotlkCmsgCatalog.NameOf(pkt.Opcode),
                pkt.Payload.Span,
                offset));
        }

        var grants = responses
            .Where(r => GrantOpcodes.ContainsKey(r.Opcode))
            .Select(r => $"{GrantOpcodes[r.Opcode]}(0x{r.Opcode:X4}) len={r.Len}")
            .Distinct()
            .ToList();

        var notify = responses
            .Where(r => r.Opcode == 0x01CB)
            .SelectMany(r => r.AsciiStrings ?? Array.Empty<string>())
            .Take(3)
            .ToList();

        // Family-aware grant matching reduces combat ambient false positives
        // (e.g. SMSG_ACHIEVEMENT_EARNED while probing MOVE_*_CHEAT mid-fight).
        var relevantGrants = grants
            .Where(g => GrantMatchesFamily(g, family))
            .ToList();

        string verdict;
        string summary;
        if (relevantGrants.Count > 0 && family is "cheat" or "gm_grant" or "quest_cheat" or "achievement_cheat")
        {
            verdict = "GAP_CRITICAL";
            summary = "GRANT after privileged/cheat CMSG → " + string.Join(", ", relevantGrants);
            Interlocked.Increment(ref _gaps);
        }
        else if (relevantGrants.Count > 0 && family is "loot" or "quest_legit" or "vendor" or "mail" or "trade")
        {
            verdict = "GAP_WARN";
            summary = "Economy SMSG after " + family + " CMSG (verify context) → " + string.Join(", ", relevantGrants);
            Interlocked.Increment(ref _gaps);
        }
        else if (grants.Count > 0)
        {
            // Grant-class SMSG seen but not family-matched → ambient / review
            verdict = "NEEDS_REVIEW";
            summary = "ambient grant window (not family-matched) → " + string.Join(", ", grants);
        }
        else if (notify.Count > 0)
        {
            verdict = "HARDENED_NOTIFY";
            summary = "notification: " + string.Join(" | ", notify);
        }
        else if (responses.All(r => NoiseSmsg.Contains(r.Opcode) || IsMoveFamily(r.Opcode)) || responses.Count == 0)
        {
            verdict = "HARDENED_SILENT";
            summary = responses.Count == 0 ? "no SMSG in window (ignored)" : "ambient noise only";
        }
        else
        {
            var rare = responses
                .Where(r => !NoiseSmsg.Contains(r.Opcode) && !IsMoveFamily(r.Opcode))
                .Take(4)
                .Select(r => $"{r.Name}(0x{r.Opcode:X4})")
                .ToList();
            verdict = "NEEDS_REVIEW";
            summary = rare.Count > 0 ? string.Join(", ", rare) : "non-grant responses";
        }

        var finding = new Finding(
            before,
            opcode,
            name,
            family,
            variant.Label,
            Convert.ToHexString(variant.Payload),
            verdict,
            summary,
            grants,
            responses);

        _findings.Enqueue(finding);
        while (_findings.Count > 800 && _findings.TryDequeue(out _))
        {
        }

        if (verdict.StartsWith("GAP", StringComparison.Ordinal))
        {
            Log($"GAP 0x{opcode:X4} {name} [{family}/{variant.Label}] → {summary}");
        }
        else if (verdict == "NEEDS_REVIEW")
        {
            Log($"REVIEW 0x{opcode:X4} {name} → {summary}");
        }
    }

    private IReadOnlyList<ProbeVariant> BuildAuditVariants(uint opcode, string name)
    {
        var list = new List<ProbeVariant>();
        var self = _pool.SelfGuid;
        var target = _pool.NextGuidOr(self);
        var entry = _pool.NextEntryOr(1);

        switch (name)
        {
            case "CMSG_CHEAT_SETMONEY":
                list.Add(V("money=1", U32(1), "tiny gold probe"));
                list.Add(V("money=0", U32(0), "zero gold"));
                break;
            case "CMSG_LEVEL_CHEAT":
            case "CMSG_PET_LEVEL_CHEAT":
                list.Add(V("level=1", U32(1), "level 1"));
                list.Add(V("level=60", U32(60), "level 60"));
                break;
            case "CMSG_XP_CHEAT":
                list.Add(V("xp=1", U32(1), "1 xp"));
                break;
            case "CMSG_FLAG_QUEST":
            case "CMSG_FLAG_QUEST_FINISH":
            case "CMSG_CLEAR_QUEST":
                foreach (var q in new uint[] { 1, 2, 5, entry })
                    list.Add(V($"quest={q}", U32(q), name));
                break;
            case "CMSG_COMPLETE_ACHIEVEMENT_CHEAT":
            case "CMSG_SET_CRITERIA_CHEAT":
                list.Add(V("ach=1", U32(1), "achievement 1"));
                break;
            case "CMSG_CREATEITEM":
            case "CMSG_GM_CREATE_ITEM_TARGET":
                list.Add(V("item=25", Concat(U32(25), U32(1)), "Worn Shortsword x1"));
                break;
            case "CMSG_GODMODE":
            case "CMSG_PETGODMODE":
            case "CMSG_CHEAT_SET_HONOR_CURRENCY":
            case "CMSG_CHEAT_SET_ARENA_CURRENCY":
                list.Add(V("u32=1", U32(1), "enable/set 1"));
                list.Add(V("empty", Array.Empty<byte>(), "empty"));
                break;
            case "CMSG_QUESTGIVER_COMPLETE_QUEST":
            case "CMSG_QUESTGIVER_REQUEST_REWARD":
            case "CMSG_QUESTGIVER_CHOOSE_REWARD":
            case "CMSG_QUESTGIVER_ACCEPT_QUEST":
                list.Add(V("guid+quest", Concat(GuidBytes(target), U32(1)), "npc+quest1"));
                break;
            case "CMSG_LOOT_MONEY":
            case "CMSG_LOOT":
            case "CMSG_AUTOSTORE_LOOT_ITEM":
                list.Add(V("guid", GuidBytes(target), $"guid={target:X16}"));
                list.Add(V("slot0", U32(0), "slot 0"));
                break;
            case "CMSG_MAIL_TAKE_MONEY":
            case "CMSG_MAIL_TAKE_ITEM":
                list.Add(V("mailbox", Concat(GuidBytes(target), U32(1)), "mail id 1"));
                break;
            default:
                // Prefer factory generics but cap to 4 to keep audit fast
                foreach (var v in ProbePayloadFactory.BuildVariants(opcode, name, _pool, includeGeneric: true).Take(4))
                    list.Add(v);
                break;
        }

        if (list.Count == 0)
        {
            list.Add(V("empty", Array.Empty<byte>(), "empty"));
            list.Add(V("u32=1", U32(1), "u32 1"));
        }

        return list.Take(4).ToList();
    }

    private static string? ClassifyFamily(string name)
    {
        if (name.Contains("CHEAT", StringComparison.OrdinalIgnoreCase)
            || name is "CMSG_GODMODE" or "CMSG_PETGODMODE" or "CMSG_CREATEITEM"
            || name.Contains("GODMODE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SETMONEY", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LEVEL_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("XP_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("COOLDOWN_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WEATHER_SPEED_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MOVE_CHARACTER_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TELEPORT_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SPEED_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("COLLISION_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GRAVITY", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SET_FACTION_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("USE_SKILL_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DISABLE_PVP_CHEAT", StringComparison.OrdinalIgnoreCase))
        {
            return "cheat";
        }

        if (name is "CMSG_FLAG_QUEST" or "CMSG_FLAG_QUEST_FINISH" or "CMSG_CLEAR_QUEST"
            || name.Contains("QUEST_CHEAT", StringComparison.OrdinalIgnoreCase))
        {
            return "quest_cheat";
        }

        if (name.Contains("ACHIEVEMENT_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CRITERIA_CHEAT", StringComparison.OrdinalIgnoreCase)
            || name is "CMSG_COMPLETE_ACHIEVEMENT_CHEAT" or "CMSG_SET_CRITERIA_CHEAT")
        {
            return "achievement_cheat";
        }

        if (name.StartsWith("CMSG_GM_", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("CREATE", StringComparison.OrdinalIgnoreCase)
                || name.Contains("GRANT", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TEACH", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RESTORE", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SUMMON", StringComparison.OrdinalIgnoreCase)
                || name.Contains("CHARACTER", StringComparison.OrdinalIgnoreCase)))
        {
            return "gm_grant";
        }

        if (name.Contains("QUESTGIVER_COMPLETE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("QUESTGIVER_REQUEST_REWARD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("QUESTGIVER_CHOOSE_REWARD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("QUESTGIVER_ACCEPT", StringComparison.OrdinalIgnoreCase)
            || name is "CMSG_START_QUEST" or "CMSG_AUTO_QUEST_SHOW_COMPLETE")
        {
            return "quest_legit";
        }

        if (name.Contains("LOOT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AUTOSTORE", StringComparison.OrdinalIgnoreCase))
        {
            return "loot";
        }

        if (name.Contains("BUY", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SELL", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VENDOR", StringComparison.OrdinalIgnoreCase)
            || name.Contains("BUYBACK", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TRAINER_BUY", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PURCHASE", StringComparison.OrdinalIgnoreCase))
        {
            return "vendor";
        }

        if (name.Contains("MAIL_TAKE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MAIL_CREATE", StringComparison.OrdinalIgnoreCase)
            || name is "CMSG_SEND_MAIL")
        {
            return "mail";
        }

        if (name.Contains("TRADE", StringComparison.OrdinalIgnoreCase))
        {
            return "trade";
        }

        if (name.Contains("RECOVER", StringComparison.OrdinalIgnoreCase)
            || name.Contains("REFUND", StringComparison.OrdinalIgnoreCase))
        {
            return "recover";
        }

        if (name.Contains("TOGGLE_XP", StringComparison.OrdinalIgnoreCase)
            || name.Contains("XP_GAIN", StringComparison.OrdinalIgnoreCase))
        {
            return "xp_toggle";
        }

        return null;
    }

    private static int FamilyPriority(string family) => family switch
    {
        "cheat" => 0,
        "quest_cheat" => 1,
        "achievement_cheat" => 2,
        "gm_grant" => 3,
        "recover" => 4,
        "xp_toggle" => 5,
        "quest_legit" => 6,
        "loot" => 7,
        "vendor" => 8,
        "mail" => 9,
        "trade" => 10,
        _ => 99
    };

    /// <summary>
    /// True when the grant SMSG label is economically meaningful for this CMSG family.
    /// Prevents combat achievements / loot noise from flagging unrelated cheat probes.
    /// </summary>
    private static bool GrantMatchesFamily(string grantLabel, string family)
    {
        var g = grantLabel.ToUpperInvariant();
        return family switch
        {
            "achievement_cheat" => g.Contains("ACHIEVEMENT", StringComparison.Ordinal),
            "quest_cheat" or "quest_legit" =>
                g.Contains("QUEST", StringComparison.Ordinal) || g.Contains("XP", StringComparison.Ordinal)
                || g.Contains("ITEM_PUSH", StringComparison.Ordinal),
            "loot" =>
                g.Contains("LOOT", StringComparison.Ordinal) || g.Contains("ITEM_PUSH", StringComparison.Ordinal)
                || g.Contains("MONEY", StringComparison.Ordinal),
            "vendor" => g.Contains("BUY", StringComparison.Ordinal) || g.Contains("SELL", StringComparison.Ordinal)
                || g.Contains("ITEM_PUSH", StringComparison.Ordinal),
            "mail" => g.Contains("MAIL", StringComparison.Ordinal) || g.Contains("ITEM_PUSH", StringComparison.Ordinal)
                || g.Contains("MONEY", StringComparison.Ordinal),
            "trade" => g.Contains("TRADE", StringComparison.Ordinal) || g.Contains("ITEM_PUSH", StringComparison.Ordinal),
            // Generic cheat/gm: XP/gold/items/quest only — not ambient achievements mid-fight
            "cheat" or "gm_grant" or "recover" or "xp_toggle" =>
                g.Contains("XP", StringComparison.Ordinal) || g.Contains("MONEY", StringComparison.Ordinal)
                || g.Contains("ITEM_PUSH", StringComparison.Ordinal) || g.Contains("LOOT", StringComparison.Ordinal)
                || g.Contains("BUY", StringComparison.Ordinal) || g.Contains("QUEST", StringComparison.Ordinal),
            _ => true
        };
    }

    private static bool IsMoveFamily(uint opcode) =>
        (opcode >= 0x00B5 && opcode <= 0x00DF)
        || (opcode >= 0x00E1 && opcode <= 0x00EE);

    private IWorldClient RequireWorld()
    {
        lock (_gate)
        {
            if (_world is null || _world.State != SessionState.InWorld)
            {
                throw new InvalidOperationException("World not ready for audit.");
            }

            return _world;
        }
    }

    private void Log(string line)
    {
        var msg = $"{DateTimeOffset.Now:HH:mm:ss} {line}";
        _log.Enqueue(msg);
        while (_log.Count > 500 && _log.TryDequeue(out _))
        {
        }

        Console.WriteLine($"[audit] {line}");
    }

    private static ProbeVariant V(string label, byte[] payload, string notes) =>
        new(label, ProbeTemplateKind.Known, payload, notes);

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] GuidBytes(ulong g)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, g);
        return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var b = new byte[len];
        var o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, b, o, p.Length);
            o += p.Length;
        }

        return b;
    }
}
