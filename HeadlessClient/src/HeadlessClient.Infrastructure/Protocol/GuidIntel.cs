namespace HeadlessClient.Infrastructure.Protocol;

static class GuidIntel
{
    public static uint EntryFromGuid(ulong guid)
    {
        if (guid == 0) return 0;
        return (uint)((guid >> 24) & 0xFFFFFF);
    }
}
