using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.World;
using MoonSharp.Interpreter;

namespace HeadlessClient.Infrastructure.Lua;

public sealed class ExtProxyNativeStubs
{
    private readonly IObjectDirectory _objects;
    private readonly IWorldActions _world;
    private readonly List<ulong> _lootable = new();
    private uint _hacks;
    private uint _flyhack;
    private double _speedScale = 1.0;
    private uint _nofall;
    private uint _antiAfk;

    public ExtProxyNativeStubs(IObjectDirectory objects, IWorldActions world)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public void Register(Script script)
    {
        ArgumentNullException.ThrowIfNull(script);

        script.Globals["GmObjectCount"] = (Func<double>)(() => _objects.Snapshot().Count);

        script.Globals["GmObjectGuid"] = (Func<double, DynValue>)(index =>
        {
            var list = _objects.Snapshot().ToList();
            var i = (int)index;
            if (i < 1 || i > list.Count)
            {
                return DynValue.Nil;
            }

            return DynValue.NewString(FormatGuid(list[i - 1].Guid));
        });

        script.Globals["GmObjectByGuid"] = (Func<string, DynValue>)(guidText =>
        {
            if (!TryParseGuid(guidText, out var guid))
            {
                return DynValue.Nil;
            }

            var obj = _objects.Snapshot().FirstOrDefault(o => o.Guid == guid);
            if (obj is null)
            {
                return DynValue.Nil;
            }

            return DynValue.NewTable(BuildObjectTable(script, obj));
        });

        script.Globals["GmTeleport"] = (Func<double, double, double, double, DynValue>)((x, y, z, o) =>
        {
            _ = _world.MoveFallLandAsync((float)x, (float)y, (float)z, (float)o, CancellationToken.None);
            return DynValue.NewNumber(1);
        });

        script.Globals["GmTeleportRaw"] = (Func<double, double, double, double, DynValue>)((x, y, z, o) =>
        {
            _ = _world.MoveFallLandAsync((float)x, (float)y, (float)z, (float)o, CancellationToken.None);
            return DynValue.NewNumber(1);
        });

        script.Globals["GmLootableCount"] = (Func<double, double>)(radius =>
        {
            _ = radius;
            _lootable.Clear();
            return _lootable.Count;
        });

        script.Globals["GmLootableGuid"] = (Func<double, DynValue>)(index =>
        {
            var i = (int)index;
            if (i < 1 || i > _lootable.Count)
            {
                return DynValue.Nil;
            }

            return DynValue.NewString(FormatGuid(_lootable[i - 1]));
        });

        script.Globals["GmGetHacks"] = (Func<DynValue>)(() =>
            DynValue.NewTuple(
                DynValue.NewNumber(_hacks),
                DynValue.NewNumber(_flyhack),
                DynValue.NewNumber(_speedScale)));

        script.Globals["GmFlyhack"] = (Func<double, double>)(on =>
        {
            _flyhack = on != 0 ? 1u : 0u;
            if (_flyhack != 0)
            {
                _hacks |= 1u;
            }
            else
            {
                _hacks &= ~1u;
            }

            return _flyhack;
        });

        script.Globals["GmNofall"] = (Func<double, double>)(on =>
        {
            _nofall = on != 0 ? 1u : 0u;
            if (_nofall != 0)
            {
                _hacks |= 2u;
            }
            else
            {
                _hacks &= ~2u;
            }

            return _nofall;
        });

        script.Globals["GmAntiAfk"] = (Func<double, double>)(on =>
        {
            _antiAfk = on != 0 ? 1u : 0u;
            if (_antiAfk != 0)
            {
                _hacks |= 4u;
            }
            else
            {
                _hacks &= ~4u;
            }

            return _antiAfk;
        });
    }

    private static Table BuildObjectTable(Script script, WorldObject obj)
    {
        var t = new Table(script);
        t["guid"] = FormatGuid(obj.Guid);
        t["name"] = obj.Name ?? string.Empty;
        t["entry"] = obj.Entry;
        t["x"] = obj.X;
        t["y"] = obj.Y;
        t["z"] = obj.Z;
        t["o"] = obj.Orientation;
        t["health"] = obj.Health;
        t["maxHealth"] = obj.MaxHealth;
        t["typeId"] = obj.TypeId;
        return t;
    }

    private static string FormatGuid(ulong guid) => guid.ToString("X16");

    private static bool TryParseGuid(string? text, out ulong guid)
    {
        guid = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out guid);
    }
}
