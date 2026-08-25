using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Infrastructure.Auth;
using HeadlessClient.Infrastructure.Chat;
using HeadlessClient.Infrastructure.Config;
using HeadlessClient.Infrastructure.Fleet;
using HeadlessClient.Infrastructure.Logging;
using HeadlessClient.Infrastructure.Lua;
using HeadlessClient.Infrastructure.Monitoring;
using HeadlessClient.Infrastructure.Probe;
using HeadlessClient.Infrastructure.Protocol;
using HeadlessClient.Infrastructure.Query;
using HeadlessClient.Infrastructure.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HeadlessClient.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddHeadlessInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<HeadlessOptions>(configuration);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<HeadlessOptions>>().Value);
        services.AddSingleton<IHeadlessOptions>(sp => sp.GetRequiredService<HeadlessOptions>());
        services.AddSingleton<ICredentialStore, ConfigurationCredentialStore>();
        services.AddSingleton<IAuthClient, TcpAuthClient>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<HeadlessOptions>();
            return new PacketWireLogger(opts.PacketLog);
        });
        services.AddSingleton<IWorldClient>(sp =>
        {
            var opts = sp.GetRequiredService<IHeadlessOptions>();
            var creds = sp.GetRequiredService<ICredentialStore>();
            var log = sp.GetRequiredService<PacketWireLogger>();
            return new TcpWorldClient(opts, creds, log);
        });
        services.AddSingleton<IWorldActions, WorldActionService>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<HeadlessOptions>();
            var path = string.IsNullOrWhiteSpace(opts.ChatDbPath) ? null : opts.ChatDbPath;
            return new Persistence.SqliteChatStore(path);
        });
        services.AddSingleton<PersistentChatLog>(sp =>
            new PersistentChatLog(sp.GetRequiredService<Persistence.SqliteChatStore>()));
        services.AddSingleton<IChatLog>(sp => sp.GetRequiredService<PersistentChatLog>());
        services.AddSingleton<IChatHistory>(sp => sp.GetRequiredService<PersistentChatLog>());
        services.AddSingleton<IObservableChatLog>(sp => sp.GetRequiredService<PersistentChatLog>());
        services.AddSingleton<PlayerDirectory>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<HeadlessOptions>();
            var path = string.IsNullOrWhiteSpace(opts.PlayerRosterPath) ? null : opts.PlayerRosterPath;
            return new PlayerRosterStore(path);
        });
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<HeadlessOptions>();
            var path = string.IsNullOrWhiteSpace(opts.ChannelRosterPath) ? null : opts.ChannelRosterPath;
            return new ChannelRosterStore(path);
        });
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<HeadlessOptions>();
            var path = string.IsNullOrWhiteSpace(opts.GameDataDbPath) ? null : opts.GameDataDbPath;
            return new GameDataCatalog(path);
        });
        services.AddSingleton<QueryCache>();
        services.AddSingleton<ChatMediator>();
        services.AddSingleton<PlayerProfileService>();
        services.AddSingleton<OpcodeProbeService>();
        services.AddSingleton<EconomySecurityAudit>();
        services.AddSingleton<InMemoryObjectDirectory>();
        services.AddSingleton<IObjectDirectory>(sp => sp.GetRequiredService<InMemoryObjectDirectory>());
        services.AddSingleton<WorldIntelService>();
        services.AddSingleton<LootCollectorLc1Decoder>();
        services.AddSingleton<UpdateObjectProjector>();
        services.AddSingleton<WorldInboundProjector>();
        services.AddSingleton<Lua51AddonHost>();
        services.AddSingleton<IAddonHost>(sp => sp.GetRequiredService<Lua51AddonHost>());
        services.AddSingleton<AccountFleetService>();
        services.AddHostedService(sp => sp.GetRequiredService<AccountFleetService>());
        return services;
    }
}
