namespace AscensionNetTool;

static class PacketFuzzMutator
{
    static readonly byte[] ByteProbes =
        { 0x00, 0x01, 0x02, 0x03, 0x07, 0x0F, 0x10, 0x20, 0x40, 0x7F, 0x80, 0xFE, 0xFF };

    public static IEnumerable<FuzzVariant> Generate(
        byte[] seed,
        uint opcode,
        IReadOnlyList<DetectedField> fields,
        PacketFuzzSettings s,
        HashSet<string> blacklist,
        OmValuePool? pool = null,
        long startIndex = 0)
    {
        long idx = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            PacketStructure.Fingerprint(seed),
        };

        IEnumerable<FuzzVariant> Emit(byte[] pkt, string strategy, string desc, int fieldOff)
        {
            if (pkt.Length == 0 || pkt.Length > IpcConstants.ReplayMax)
                yield break;
            if (s.RefreshDynamicFields && pool is not null)
                pkt = PacketForge.Legitimize(pkt, pool);
            string fp = PacketStructure.Fingerprint(pkt);
            if (!seen.Add(fp) || blacklist.Contains(fp))
                yield break;
            long i = idx++;
            if (i < startIndex)
                yield break;
            yield return new FuzzVariant
            {
                Packet = pkt,
                Strategy = strategy,
                Description = desc,
                FieldOffset = fieldOff,
                Index = i,
            };
        }

        foreach (var v in Emit((byte[])seed.Clone(), "baseline", "original packet", -1))
            yield return v;

        // OM substitution into detected seed fields
        if (s.OmInjectIntoSeed && pool is not null)
        {
            foreach (var f in fields)
            {
                if (f.Kind == FuzzFieldKind.Opcode) continue;
                if (f.Kind == FuzzFieldKind.Guid && f.Offset + 8 <= seed.Length)
                {
                    foreach (var g in pool.Guids().Take(s.OmGuidBudget))
                    {
                        var p = (byte[])seed.Clone();
                        BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 8), g.Guid);
                        foreach (var v in Emit(p, "om-sub-guid", $"+{f.Offset:X4} ← {g.Label}", f.Offset))
                            yield return v;
                    }
                }
                if (f.Kind is FuzzFieldKind.U32 or FuzzFieldKind.I32 && f.Offset + 4 <= seed.Length)
                {
                    foreach (var e in pool.Atoms.Where(a => a.Kind is BruteAtomKind.Entry32 or BruteAtomKind.U32 or BruteAtomKind.Tick32)
                                 .Take(s.OmEntryBudget + s.OmU32Budget))
                    {
                        var p = (byte[])seed.Clone();
                        BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 4), e.U32);
                        foreach (var v in Emit(p, "om-sub-u32", $"+{f.Offset:X4} ← {e.Label}", f.Offset))
                            yield return v;
                    }
                }
                if (f.Kind == FuzzFieldKind.Float && f.Offset + 4 <= seed.Length)
                {
                    foreach (var c in pool.Coords().Take(s.OmCoordBudget))
                    {
                        float val = c.Kind == BruteAtomKind.Float ? c.F0 : c.F0;
                        var p = (byte[])seed.Clone();
                        BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 4), val);
                        foreach (var v in Emit(p, "om-sub-float", $"+{f.Offset:X4} ← {c.Label}", f.Offset))
                            yield return v;
                    }
                    // If we have xyz and consecutive floats, try writing full xyz
                    if (f.Offset + 12 <= seed.Length)
                    {
                        foreach (var c in pool.Atoms.Where(a => a.Kind == BruteAtomKind.Xyz).Take(4))
                        {
                            var p = (byte[])seed.Clone();
                            BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 4), c.F0);
                            BitConverter.TryWriteBytes(p.AsSpan(f.Offset + 4, 4), c.F1);
                            BitConverter.TryWriteBytes(p.AsSpan(f.Offset + 8, 4), c.F2);
                            foreach (var v in Emit(p, "om-sub-xyz", $"+{f.Offset:X4} xyz ← {c.Label}", f.Offset))
                                yield return v;
                        }
                    }
                }
                if (f.Kind == FuzzFieldKind.Bytes && f.Size >= 4)
                {
                    foreach (var str in pool.Strings().Where(a => a.Kind == BruteAtomKind.CString).Take(s.OmStringBudget))
                    {
                        var raw = System.Text.Encoding.ASCII.GetBytes(str.Text);
                        if (raw.Length + 1 > f.Size) continue;
                        var p = (byte[])seed.Clone();
                        Array.Clear(p, f.Offset, f.Size);
                        Buffer.BlockCopy(raw, 0, p, f.Offset, raw.Length);
                        foreach (var v in Emit(p, "om-sub-str", $"+{f.Offset:X4} ← {str.Label}", f.Offset))
                            yield return v;
                    }
                }
            }
        }

        foreach (var f in fields)
        {
            if (f.Kind == FuzzFieldKind.Opcode)
                continue;

            if (f.Kind == FuzzFieldKind.U8 && s.MutateBytes)
            {
                byte orig = seed[f.Offset];
                foreach (byte b in ByteProbes)
                {
                    if (b == orig) continue;
                    var p = (byte[])seed.Clone();
                    p[f.Offset] = b;
                    foreach (var v in Emit(p, "u8-probe", $"+{f.Offset:X4} u8 {orig:X2}->{b:X2}", f.Offset))
                        yield return v;
                }
                for (int d = -3; d <= 3; d++)
                {
                    if (d == 0) continue;
                    int n = orig + d;
                    if (n is < 0 or > 255) continue;
                    var p = (byte[])seed.Clone();
                    p[f.Offset] = (byte)n;
                    foreach (var v in Emit(p, "u8-inc", $"+{f.Offset:X4} u8 {orig}+{d}", f.Offset))
                        yield return v;
                }
            }

            if (f.Kind == FuzzFieldKind.U16 && s.MutateU16 && f.Offset + 2 <= seed.Length)
            {
                ushort orig = BitConverter.ToUInt16(seed, f.Offset);
                foreach (ushort x in new ushort[]
                         {
                             0, 1, 2, unchecked((ushort)(orig + 1)), unchecked((ushort)(orig - 1)), 0xFFFF, 0x7FFF,
                         }.Distinct())
                {
                    if (x == orig) continue;
                    var p = (byte[])seed.Clone();
                    BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 2), x);
                    foreach (var v in Emit(p, "u16", $"+{f.Offset:X4} u16 {orig}->{x}", f.Offset))
                        yield return v;
                }
            }

            if ((f.Kind is FuzzFieldKind.U32 or FuzzFieldKind.I32) && s.MutateU32 && f.Offset + 4 <= seed.Length)
            {
                uint orig = BitConverter.ToUInt32(seed, f.Offset);
                foreach (uint x in new uint[]
                         {
                             0, 1, 2, orig + 1, orig - 1, 0x7FFFFFFF, 0xFFFFFFFF, 0x10000, 0x1000000,
                         }.Distinct())
                {
                    if (x == orig) continue;
                    var p = (byte[])seed.Clone();
                    BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 4), x);
                    foreach (var v in Emit(p, "u32", $"+{f.Offset:X4} u32 {orig:X8}->{x:X8}", f.Offset))
                        yield return v;
                }
                if (s.MutateInts)
                {
                    int oi = unchecked((int)orig);
                    foreach (int delta in new[] { -100, -10, -1, 1, 10, 100, 1000 })
                    {
                        var p = (byte[])seed.Clone();
                        BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 4), oi + delta);
                        foreach (var v in Emit(p, "i32", $"+{f.Offset:X4} i32 {oi}+{delta}", f.Offset))
                            yield return v;
                    }
                }
            }

            if (f.Kind == FuzzFieldKind.Float && s.MutateFloats && f.Offset + 4 <= seed.Length)
            {
                float orig = BitConverter.ToSingle(seed, f.Offset);
                foreach (float x in new[] { 0f, -orig, orig + 1f, orig - 1f, orig * 2f, orig * 0.5f, 9999f, -9999f })
                {
                    if (float.IsNaN(x) || Math.Abs(x - orig) < 1e-6f) continue;
                    var p = (byte[])seed.Clone();
                    BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 4), x);
                    foreach (var v in Emit(p, "float", $"+{f.Offset:X4} float {orig:G}->{x:G}", f.Offset))
                        yield return v;
                }
            }

            if (f.Kind == FuzzFieldKind.Guid && s.MutateGuids && f.Offset + 8 <= seed.Length)
            {
                ulong orig = BitConverter.ToUInt64(seed, f.Offset);
                ulong lo = orig & 0xFFFFFFFFUL;
                foreach (ulong x in new ulong[]
                         {
                             0, 1, orig + 1, orig - 1, lo, lo + 1, (orig & 0xFFFFFFFF00000000UL) | 1,
                         }.Distinct())
                {
                    if (x == orig) continue;
                    var p = (byte[])seed.Clone();
                    BitConverter.TryWriteBytes(p.AsSpan(f.Offset, 8), x);
                    foreach (var v in Emit(p, "guid", $"+{f.Offset:X4} guid {orig:X16}->{x:X16}", f.Offset))
                        yield return v;
                }
            }

            if (s.BitFlips && f.Size > 0)
            {
                for (int b = 0; b < Math.Min(f.Size, 4); b++)
                {
                    for (int bit = 0; bit < 8; bit++)
                    {
                        var p = (byte[])seed.Clone();
                        p[f.Offset + b] ^= (byte)(1 << bit);
                        foreach (var v in Emit(p, "bitflip", $"+{f.Offset + b:X4} bit{bit}", f.Offset + b))
                            yield return v;
                    }
                }
            }
        }

        if (s.MutatePairs)
        {
            var body = fields.Where(f => f.Kind is FuzzFieldKind.U32 or FuzzFieldKind.U16 or FuzzFieldKind.Guid).ToList();
            var tmp = new byte[8];
            for (int i = 0; i + 1 < body.Count; i++)
            {
                var a = body[i];
                var b = body[i + 1];
                if (a.Size != b.Size || a.Size > 8) continue;
                var p = (byte[])seed.Clone();
                Buffer.BlockCopy(p, a.Offset, tmp, 0, a.Size);
                Buffer.BlockCopy(p, b.Offset, p, a.Offset, a.Size);
                Buffer.BlockCopy(tmp, 0, p, b.Offset, a.Size);
                foreach (var v in Emit(p, "pair-swap", $"+{a.Offset:X4}<->+{b.Offset:X4}", a.Offset))
                    yield return v;
            }
        }

        if (s.FuzzyRandom)
        {
            var rng = new Random(unchecked((int)opcode) ^ (seed.Length * 397));
            int body = PacketStructure.BodyStart(seed, opcode);
            int budget = Math.Max(0, s.RandomBudget);
            for (int n = 0; n < budget; n++)
            {
                var p = (byte[])seed.Clone();
                int mutations = 1 + rng.Next(1, 4);
                for (int m = 0; m < mutations; m++)
                {
                    if (body >= p.Length) break;
                    int off = rng.Next(body, p.Length);
                    p[off] = (byte)rng.Next(256);
                }
                foreach (var v in Emit(p, "fuzzy", $"rand#{n} x{mutations}", -1))
                    yield return v;
            }
        }

        // Opcode+OM layout brute (works even with empty/minimal seed)
        if (s.OpcodeBruteMode && pool is not null)
        {
            foreach (var v in OpcodeBruteBuilder.Generate(opcode, pool, s, blacklist, startIndex: 0))
            {
                // re-index through Emit so startIndex/blacklist/seen apply
                foreach (var e in Emit(v.Packet, v.Strategy, v.Description, v.FieldOffset))
                    yield return e;
            }
        }
    }
}
