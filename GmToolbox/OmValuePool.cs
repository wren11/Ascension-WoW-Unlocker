using System.Text;

namespace AscensionNetTool;

/// <summary>A typed atom used when synthesizing unknown CMSG layouts.</summary>
enum BruteAtomKind : byte
{
    Guid64 = 1,
    PackedGuid = 2,
    U8 = 3,
    U16 = 4,
    U32 = 5,
    Float = 6,
    Xyz = 7,
    XyzO = 8,
    CString = 9,
    LenU8String = 10,
    LenU16String = 11,
    Entry32 = 12,
    Tick32 = 13,
    Empty = 14,
}

sealed class OmAtom
{
    public required BruteAtomKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Source { get; init; }
    public ulong Guid { get; init; }
    public uint U32 { get; init; }
    public float F0 { get; init; }
    public float F1 { get; init; }
    public float F2 { get; init; }
    public float F3 { get; init; }
    public string Text { get; init; } = "";
    public byte[]? Raw { get; init; }
}

sealed class OmSnapshot
{
    public uint Pid { get; init; }
    public string Label { get; init; } = "";
    public ulong PlayerGuid { get; init; }
    public float PlayerX { get; init; }
    public float PlayerY { get; init; }
    public float PlayerZ { get; init; }
    public float PlayerO { get; init; }
    public ulong TargetGuid { get; init; }
    public List<ObjUnit> Units { get; init; } = new();
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Harvests GUIDs / coords / entries / strings from live ObjectManagers
/// (current + other Ascension.go instances) and from sniffed packets.
/// </summary>
sealed class OmValuePool
{
    public List<OmSnapshot> Instances { get; } = new();
    public List<OmAtom> Atoms { get; } = new();
    public ulong SelfGuid { get; private set; }
    public float SelfX { get; private set; }
    public float SelfY { get; private set; }
    public float SelfZ { get; private set; }
    public float SelfO { get; private set; }
    public uint LastTick { get; private set; }

    public void Clear()
    {
        Instances.Clear();
        Atoms.Clear();
    }

    public void Refresh(
        ProxyClient primary,
        IReadOnlyList<CapturedPacket>? recentPackets = null,
        bool scanOtherInstances = true)
    {
        Clear();
        LastTick = (uint)Environment.TickCount;

        try
        {
            if (primary.Connected)
                IngestLive(primary, "live");
        }
        catch { }

        if (scanOtherInstances)
            HarvestOtherInstances(primary);

        if (recentPackets is not null)
        {
            IngestPackets(recentPackets);
            FinishPacketStrings(recentPackets);
        }

        // Always include null / sentinel probes
        Atoms.Add(new OmAtom { Kind = BruteAtomKind.U32, Label = "0", Source = "const", U32 = 0 });
        Atoms.Add(new OmAtom { Kind = BruteAtomKind.U32, Label = "1", Source = "const", U32 = 1 });
        Atoms.Add(new OmAtom { Kind = BruteAtomKind.U8, Label = "u8:0", Source = "const", U32 = 0 });
        Atoms.Add(new OmAtom { Kind = BruteAtomKind.U8, Label = "u8:1", Source = "const", U32 = 1 });
        Atoms.Add(new OmAtom { Kind = BruteAtomKind.Empty, Label = "(omit)", Source = "const" });
        Atoms.Add(new OmAtom
        {
            Kind = BruteAtomKind.Tick32,
            Label = $"tick {LastTick}",
            Source = "clock",
            U32 = LastTick,
        });
    }

    void IngestLive(ProxyClient proxy, string label)
    {
        var (hdr, units) = proxy.GetObjects();
        if (hdr.Magic == 0 && units.Length == 0)
            return;

        ulong target = 0;
        float facing = 0;
        foreach (var u in units)
        {
            if (u.Guid == hdr.PlayerGuid)
            {
                target = u.TargetGuid;
                facing = u.Facing;
                break;
            }
        }

        var snap = new OmSnapshot
        {
            Pid = ProxyDiscovery.ResolveLivePid() ?? 0,
            Label = label,
            PlayerGuid = hdr.PlayerGuid,
            PlayerX = hdr.PlayerX,
            PlayerY = hdr.PlayerY,
            PlayerZ = hdr.PlayerZ,
            PlayerO = facing,
            TargetGuid = target,
            Units = units.ToList(),
        };
        Instances.Add(snap);
        if (SelfGuid == 0 && hdr.PlayerGuid != 0)
        {
            SelfGuid = hdr.PlayerGuid;
            SelfX = hdr.PlayerX;
            SelfY = hdr.PlayerY;
            SelfZ = hdr.PlayerZ;
            SelfO = facing;
        }
        AddSnapshotAtoms(snap);
    }

    void HarvestOtherInstances(ProxyClient primary)
    {
        var live = ProxyDiscovery.ResolveLivePid();
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (!proc.ProcessName.StartsWith("Ascension.go", StringComparison.OrdinalIgnoreCase))
                    continue;
                uint pid = (uint)proc.Id;
                if (live is uint l && l == pid)
                    continue;
                if (!ProxyDiscovery.PipeReachable(pid, 200))
                    continue;

                using var side = new ProxyClient();
                if (!side.TryConnectToPid(pid))
                    continue;
                var (hdr, units) = side.GetObjects();
                if (hdr.PlayerGuid == 0 && units.Length == 0)
                    continue;
                ulong target = 0;
                float facing = 0;
                foreach (var u in units)
                {
                    if (u.Guid == hdr.PlayerGuid)
                    {
                        target = u.TargetGuid;
                        facing = u.Facing;
                        break;
                    }
                }
                var snap = new OmSnapshot
                {
                    Pid = pid,
                    Label = $"pid {pid}",
                    PlayerGuid = hdr.PlayerGuid,
                    PlayerX = hdr.PlayerX,
                    PlayerY = hdr.PlayerY,
                    PlayerZ = hdr.PlayerZ,
                    PlayerO = facing,
                    TargetGuid = target,
                    Units = units.ToList(),
                };
                Instances.Add(snap);
                AddSnapshotAtoms(snap);
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
        _ = primary;
    }

    void AddSnapshotAtoms(OmSnapshot snap)
    {
        string src = snap.Label;
        if (snap.PlayerGuid != 0)
        {
            Atoms.Add(new OmAtom
            {
                Kind = BruteAtomKind.Guid64,
                Label = $"player {snap.PlayerGuid:X16}",
                Source = src,
                Guid = snap.PlayerGuid,
            });
            Atoms.Add(new OmAtom
            {
                Kind = BruteAtomKind.XyzO,
                Label = $"pose {snap.PlayerX:F1},{snap.PlayerY:F1},{snap.PlayerZ:F1}",
                Source = src,
                F0 = snap.PlayerX,
                F1 = snap.PlayerY,
                F2 = snap.PlayerZ,
                F3 = snap.PlayerO,
            });
            Atoms.Add(new OmAtom
            {
                Kind = BruteAtomKind.Xyz,
                Label = $"xyz {snap.PlayerX:F1},{snap.PlayerY:F1},{snap.PlayerZ:F1}",
                Source = src,
                F0 = snap.PlayerX,
                F1 = snap.PlayerY,
                F2 = snap.PlayerZ,
            });
        }
        if (snap.TargetGuid != 0)
        {
            Atoms.Add(new OmAtom
            {
                Kind = BruteAtomKind.Guid64,
                Label = $"target {snap.TargetGuid:X16}",
                Source = src,
                Guid = snap.TargetGuid,
            });
        }

        int n = 0;
        foreach (var u in snap.Units.OrderBy(u => u.Dist).Take(24))
        {
            bool isPlayer = (u.TypeMask & ObjTypeMask.Player) != 0;
            string kind = isPlayer ? "player" : "unit";
            Atoms.Add(new OmAtom
            {
                Kind = BruteAtomKind.Guid64,
                Label = $"{kind} entry={u.Entry} d={u.Dist:F0} {u.Guid:X16}",
                Source = src,
                Guid = u.Guid,
            });
            if (u.Entry != 0)
            {
                Atoms.Add(new OmAtom
                {
                    Kind = BruteAtomKind.Entry32,
                    Label = $"entry {u.Entry}",
                    Source = src,
                    U32 = u.Entry,
                });
                // Synthetic names for string-encoded entry experiments
                string name = $"entry_{u.Entry}";
                Atoms.Add(new OmAtom
                {
                    Kind = BruteAtomKind.CString,
                    Label = $"cstr {name}",
                    Source = src,
                    Text = name,
                });
                Atoms.Add(new OmAtom
                {
                    Kind = BruteAtomKind.LenU8String,
                    Label = $"u8str {name}",
                    Source = src,
                    Text = name,
                });
                Atoms.Add(new OmAtom
                {
                    Kind = BruteAtomKind.LenU16String,
                    Label = $"u16str {name}",
                    Source = src,
                    Text = name,
                });
            }
            if (n++ < 8)
            {
                Atoms.Add(new OmAtom
                {
                    Kind = BruteAtomKind.Xyz,
                    Label = $"unitxyz {u.X:F1},{u.Y:F1},{u.Z:F1}",
                    Source = src,
                    F0 = u.X,
                    F1 = u.Y,
                    F2 = u.Z,
                });
            }
        }
    }

    void IngestPackets(IReadOnlyList<CapturedPacket> packets)
    {
        var guids = new HashSet<ulong>();
        var floats = new HashSet<uint>();
        var u32s = new HashSet<uint>();

        foreach (var p in packets.TakeLast(200))
        {
            var d = p.Data;
            int body = PacketStructure.BodyStart(d, p.Opcode);
            for (int o = body; o + 8 <= d.Length; o++)
            {
                if (LooksGuid(d, o))
                {
                    ulong g = BitConverter.ToUInt64(d, o);
                    if (guids.Add(g))
                    {
                        Atoms.Add(new OmAtom
                        {
                            Kind = BruteAtomKind.Guid64,
                            Label = $"sniff guid {g:X16}",
                            Source = $"pkt 0x{p.Opcode:X4}",
                            Guid = g,
                        });
                    }
                }
            }
            for (int o = body; o + 4 <= d.Length; o += 1)
            {
                float f = BitConverter.ToSingle(d, o);
                if (!float.IsFinite(f)) continue;
                float a = Math.Abs(f);
                if (a > 0.01f && a < 20000f)
                {
                    uint bits = BitConverter.ToUInt32(d, o);
                    if (floats.Add(bits))
                    {
                        Atoms.Add(new OmAtom
                        {
                            Kind = BruteAtomKind.Float,
                            Label = $"sniff f {f:G6}",
                            Source = $"pkt 0x{p.Opcode:X4}",
                            F0 = f,
                            U32 = bits,
                        });
                    }
                }
                uint u = BitConverter.ToUInt32(d, o);
                if (u != 0 && u < 0x1000000 && u32s.Add(u) && u32s.Count < 80)
                {
                    Atoms.Add(new OmAtom
                    {
                        Kind = BruteAtomKind.U32,
                        Label = $"sniff u32 {u}",
                        Source = $"pkt 0x{p.Opcode:X4}",
                        U32 = u,
                    });
                }
            }
        }
    }

    public void FinishPacketStrings(IReadOnlyList<CapturedPacket> packets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in packets.TakeLast(120))
        {
            var d = p.Data;
            int body = PacketStructure.BodyStart(d, p.Opcode);
            int o = body;
            while (o < d.Length)
            {
                int n = 0;
                while (o + n < d.Length && d[o + n] >= 0x20 && d[o + n] < 0x7F) n++;
                if (n >= 3)
                {
                    string s = Encoding.ASCII.GetString(d, o, Math.Min(n, 64));
                    if (seen.Add(s))
                    {
                        Atoms.Add(new OmAtom
                        {
                            Kind = BruteAtomKind.CString,
                            Label = $"sniff \"{TrimLabel(s)}\"",
                            Source = $"pkt 0x{p.Opcode:X4}",
                            Text = s,
                        });
                        Atoms.Add(new OmAtom
                        {
                            Kind = BruteAtomKind.LenU8String,
                            Label = $"sniff u8\"{TrimLabel(s)}\"",
                            Source = $"pkt 0x{p.Opcode:X4}",
                            Text = s,
                        });
                        Atoms.Add(new OmAtom
                        {
                            Kind = BruteAtomKind.LenU16String,
                            Label = $"sniff u16\"{TrimLabel(s)}\"",
                            Source = $"pkt 0x{p.Opcode:X4}",
                            Text = s,
                        });
                    }
                    o += n + (o + n < d.Length && d[o + n] == 0 ? 1 : 0);
                }
                else o++;
                if (seen.Count > 30) return;
            }
        }
    }

    static string TrimLabel(string s) => s.Length <= 24 ? s : s[..24] + "…";

    static bool LooksGuid(byte[] d, int o)
    {
        if (o + 8 > d.Length) return false;
        uint hi = BitConverter.ToUInt32(d, o + 4);
        uint lo = BitConverter.ToUInt32(d, o);
        if (lo == 0 && hi == 0) return false;
        ushort tag = (ushort)(hi >> 16);
        return tag is 0xF130 or 0xF131 or 0xF150 or 0xF151 or 0xF140 or 0xF110 or 0xF120
            || (hi == 0 && lo > 0 && lo < 0x0FFFFFFF);
    }

    public IEnumerable<OmAtom> Guids() => Atoms.Where(a => a.Kind == BruteAtomKind.Guid64);
    public IEnumerable<OmAtom> Strings() =>
        Atoms.Where(a => a.Kind is BruteAtomKind.CString or BruteAtomKind.LenU8String or BruteAtomKind.LenU16String);
    public IEnumerable<OmAtom> Coords() =>
        Atoms.Where(a => a.Kind is BruteAtomKind.Xyz or BruteAtomKind.XyzO or BruteAtomKind.Float);
}

/// <summary>
/// Builds opcode+body permutations from OM atoms — constrained combinatorial
/// layouts that cover common Trinity/Ascension CMSG shapes.
/// </summary>
static class OpcodeBruteBuilder
{
    public static IEnumerable<FuzzVariant> Generate(
        uint opcode,
        OmValuePool pool,
        PacketFuzzSettings s,
        HashSet<string> blacklist,
        long startIndex = 0)
    {
        long idx = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<FuzzVariant> Emit(byte[] pkt, string strategy, string desc)
        {
            if (pkt.Length == 0 || pkt.Length > IpcConstants.ReplayMax)
                yield break;
            pkt = PacketForge.Legitimize(pkt, pool);
            string fp = PacketStructure.Fingerprint(pkt);
            if (!seen.Add(fp) || blacklist.Contains(fp))
                yield break;
            long i = idx++;
            if (i < startIndex) yield break;
            yield return new FuzzVariant
            {
                Packet = pkt,
                Strategy = strategy,
                Description = desc,
                Index = i,
            };
        }

        // Opcode-only probes (u16 + u32 framing)
        foreach (var v in Emit(Frame(opcode, Array.Empty<byte>(), u32: false), "op-only", "opcode u16 bare"))
            yield return v;
        foreach (var v in Emit(Frame(opcode, Array.Empty<byte>(), u32: true), "op-only", "opcode u32 bare"))
            yield return v;

        var guids = pool.Guids().Take(s.OmGuidBudget).ToList();
        var entries = pool.Atoms.Where(a => a.Kind == BruteAtomKind.Entry32).Take(s.OmEntryBudget).ToList();
        var coords = pool.Coords().Take(s.OmCoordBudget).ToList();
        var strings = pool.Strings().Take(s.OmStringBudget).ToList();
        var ticks = pool.Atoms.Where(a => a.Kind == BruteAtomKind.Tick32).Take(2).ToList();
        var u32s = pool.Atoms.Where(a => a.Kind == BruteAtomKind.U32).Take(s.OmU32Budget).ToList();

        // Single-field bodies
        foreach (var g in guids)
        {
            foreach (bool u32op in new[] { false, true })
            {
                foreach (var v in Emit(Frame(opcode, Encode(g), u32op), "om-guid",
                             $"{(u32op ? "u32" : "u16")}+{g.Label}"))
                    yield return v;
                foreach (var v in Emit(Frame(opcode, EncodePacked(g.Guid), u32op), "om-packed",
                             $"{(u32op ? "u32" : "u16")}+packed {g.Label}"))
                    yield return v;
            }
        }
        foreach (var e in entries)
        {
            foreach (bool u32op in new[] { false, true })
            {
                foreach (var v in Emit(Frame(opcode, Encode(e), u32op), "om-entry",
                             $"{(u32op ? "u32" : "u16")}+{e.Label}"))
                    yield return v;
            }
        }
        foreach (var c in coords)
        {
            foreach (bool u32op in new[] { false, true })
            {
                foreach (var v in Emit(Frame(opcode, Encode(c), u32op), "om-xyz",
                             $"{(u32op ? "u32" : "u16")}+{c.Label}"))
                    yield return v;
            }
        }
        foreach (var str in strings)
        {
            foreach (bool u32op in new[] { false, true })
            {
                foreach (var v in Emit(Frame(opcode, Encode(str), u32op), "om-str",
                             $"{(u32op ? "u32" : "u16")}+{str.Label}"))
                    yield return v;
            }
        }

        if (!s.BruteLayouts)
            yield break;

        // Two-field ordered pairs (guid × entry/xyz/string/tick/u32) both orders
        var partners = entries.Cast<OmAtom>()
            .Concat(coords).Concat(strings).Concat(ticks).Concat(u32s)
            .Take(40).ToList();

        foreach (var g in guids.Take(Math.Min(8, guids.Count)))
        {
            foreach (var p in partners)
            {
                foreach (var order in new[] { (g, p), (p, g) })
                {
                    foreach (int pad in s.TryPadding ? new[] { 0, 1, 2, 4 } : new[] { 0 })
                    {
                        var body = Concat(Encode(order.Item1), pad, Encode(order.Item2));
                        foreach (bool u32op in new[] { false, true })
                        {
                            foreach (var v in Emit(Frame(opcode, body, u32op), "om-pair",
                                         $"{(u32op ? "u32" : "u16")}+{order.Item1.Label}|pad{pad}|{order.Item2.Label}"))
                                yield return v;
                        }
                    }
                }
            }
        }

        // Triple: guid + xyz + tick / guid + entry + u32 — capped
        foreach (var g in guids.Take(4))
        {
            foreach (var c in coords.Take(4))
            {
                foreach (var t in ticks.DefaultIfEmpty(new OmAtom
                         { Kind = BruteAtomKind.U32, Label = "0", Source = "const", U32 = 0 }))
                {
                    foreach (var perm in Permute3(g, c, t).Take(s.MaxLayoutPerms))
                    {
                        var body = Concat(Encode(perm[0]), 0, Encode(perm[1]), 0, Encode(perm[2]));
                        foreach (var v in Emit(Frame(opcode, body, false), "om-triple",
                                     $"u16+{perm[0].Label}|{perm[1].Label}|{perm[2].Label}"))
                            yield return v;
                    }
                }
            }
        }

        // Classic movement-ish: guid + flags u32 + xyzO + tick
        foreach (var g in guids.Take(3))
        {
            foreach (var c in coords.Where(a => a.Kind == BruteAtomKind.XyzO).Take(2))
            {
                foreach (uint flags in new uint[] { 0, 1, 0x1000, 0x02000000 })
                {
                    var ms = new MemoryStream();
                    Write(ms, g);
                    WriteU32(ms, flags);
                    Write(ms, c);
                    WriteU32(ms, pool.LastTick);
                    foreach (var v in Emit(Frame(opcode, ms.ToArray(), false), "om-moveish",
                                 $"moveish {g.Label} fl={flags:X}"))
                        yield return v;
                }
            }
        }
    }

    static IEnumerable<OmAtom[]> Permute3(OmAtom a, OmAtom b, OmAtom c)
    {
        OmAtom[] items = { a, b, c };
        foreach (var p in Permute(items))
            yield return p;
    }

    static IEnumerable<OmAtom[]> Permute(OmAtom[] items)
    {
        int n = items.Length;
        var a = (OmAtom[])items.Clone();
        yield return (OmAtom[])a.Clone();
        var c = new int[n];
        int i = 0;
        while (i < n)
        {
            if (c[i] < i)
            {
                if ((i & 1) == 0) (a[0], a[i]) = (a[i], a[0]);
                else (a[c[i]], a[i]) = (a[i], a[c[i]]);
                yield return (OmAtom[])a.Clone();
                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }

    public static byte[] Frame(uint opcode, byte[] body, bool u32)
    {
        var ms = new MemoryStream(4 + body.Length);
        if (u32) WriteU32(ms, opcode);
        else WriteU16(ms, (ushort)opcode);
        ms.Write(body, 0, body.Length);
        return ms.ToArray();
    }

    static byte[] Encode(OmAtom a) => a.Kind switch
    {
        BruteAtomKind.Guid64 => BitConverter.GetBytes(a.Guid),
        BruteAtomKind.PackedGuid => EncodePacked(a.Guid),
        BruteAtomKind.U8 => new[] { (byte)a.U32 },
        BruteAtomKind.U16 => BitConverter.GetBytes((ushort)a.U32),
        BruteAtomKind.U32 or BruteAtomKind.Entry32 or BruteAtomKind.Tick32 => BitConverter.GetBytes(a.U32),
        BruteAtomKind.Float => BitConverter.GetBytes(a.F0),
        BruteAtomKind.Xyz => ConcatFloats(a.F0, a.F1, a.F2),
        BruteAtomKind.XyzO => ConcatFloats(a.F0, a.F1, a.F2, a.F3),
        BruteAtomKind.CString => EncodeCString(a.Text),
        BruteAtomKind.LenU8String => EncodeLen8(a.Text),
        BruteAtomKind.LenU16String => EncodeLen16(a.Text),
        BruteAtomKind.Empty => Array.Empty<byte>(),
        _ => a.Raw ?? Array.Empty<byte>(),
    };

    static void Write(Stream s, OmAtom a)
    {
        var b = Encode(a);
        s.Write(b, 0, b.Length);
    }

    static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BitConverter.TryWriteBytes(b, v);
        s.Write(b);
    }

    static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, v);
        s.Write(b);
    }

    static byte[] ConcatFloats(params float[] fs)
    {
        var b = new byte[fs.Length * 4];
        for (int i = 0; i < fs.Length; i++)
            BitConverter.TryWriteBytes(b.AsSpan(i * 4, 4), fs[i]);
        return b;
    }

    static byte[] EncodeCString(string s)
    {
        var raw = Encoding.ASCII.GetBytes(s ?? "");
        var b = new byte[raw.Length + 1];
        Buffer.BlockCopy(raw, 0, b, 0, raw.Length);
        return b;
    }

    static byte[] EncodeLen8(string s)
    {
        var raw = Encoding.ASCII.GetBytes(s ?? "");
        if (raw.Length > 255) raw = raw[..255];
        var b = new byte[1 + raw.Length];
        b[0] = (byte)raw.Length;
        Buffer.BlockCopy(raw, 0, b, 1, raw.Length);
        return b;
    }

    static byte[] EncodeLen16(string s)
    {
        var raw = Encoding.ASCII.GetBytes(s ?? "");
        if (raw.Length > 65535) raw = raw[..65535];
        var b = new byte[2 + raw.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), (ushort)raw.Length);
        Buffer.BlockCopy(raw, 0, b, 2, raw.Length);
        return b;
    }

    public static byte[] EncodePacked(ulong guid)
    {
        // Classic packed GUID mask + non-zero bytes
        byte mask = 0;
        var parts = new List<byte>(8);
        for (int i = 0; i < 8; i++)
        {
            byte b = (byte)((guid >> (8 * i)) & 0xFF);
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                parts.Add(b);
            }
        }
        var r = new byte[1 + parts.Count];
        r[0] = mask;
        for (int i = 0; i < parts.Count; i++)
            r[1 + i] = parts[i];
        return r;
    }

    static byte[] Concat(byte[] a, int pad, byte[] b)
    {
        var r = new byte[a.Length + pad + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length + pad, b.Length);
        return r;
    }

    static byte[] Concat(byte[] a, int pad1, byte[] b, int pad2, byte[] c)
    {
        var r = new byte[a.Length + pad1 + b.Length + pad2 + c.Length];
        int o = 0;
        Buffer.BlockCopy(a, 0, r, o, a.Length); o += a.Length + pad1;
        Buffer.BlockCopy(b, 0, r, o, b.Length); o += b.Length + pad2;
        Buffer.BlockCopy(c, 0, r, o, c.Length);
        return r;
    }
}

/// <summary>
/// Rewrites dynamic fields so forged packets look like a live client send:
/// refresh player GUID, pose floats, and tick-like u32s before NetClient::Send
/// (wire ARC4 happens inside the hooked send path).
/// </summary>
static class PacketForge
{
    public static byte[] Legitimize(byte[] pkt, OmValuePool pool)
    {
        if (pkt.Length < 2 || pool.SelfGuid == 0)
            return pkt;
        var p = (byte[])pkt.Clone();
        uint opcode = p.Length >= 4 && BitConverter.ToUInt32(p, 0) <= 0xFFFF
            ? BitConverter.ToUInt32(p, 0)
            : BitConverter.ToUInt16(p, 0);
        int body = PacketStructure.BodyStart(p, opcode);

        // Replace any GUID that matches a known stale self? Prefer inject current self
        // into first guid-looking slot when strategy is OM-built (already current).
        // Refresh tick-like values near end of small packets.
        if (p.Length - body >= 4)
        {
            int lastU32 = p.Length - 4;
            uint v = BitConverter.ToUInt32(p, lastU32);
            // Heuristic: values that look like GetTickCount-ish (ms since boot)
            if (v > 10_000 && v < 0xF0000000)
                BitConverter.TryWriteBytes(p.AsSpan(lastU32, 4), pool.LastTick != 0 ? pool.LastTick : (uint)Environment.TickCount);
        }

        // If first 8 body bytes look like our old player-ish low guid, rewrite to self
        if (body + 8 <= p.Length)
        {
            ulong g = BitConverter.ToUInt64(p, body);
            if ((g & 0xFFFFFFFFUL) != 0 && (g >> 32) == 0)
                BitConverter.TryWriteBytes(p.AsSpan(body, 8), pool.SelfGuid);
        }
        return p;
    }
}
