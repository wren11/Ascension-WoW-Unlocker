using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Auth;
using Microsoft.Extensions.Options;

namespace HeadlessClient.Infrastructure.Config;

public sealed class ConfigurationCredentialStore : ICredentialStore
{
    private readonly HeadlessOptions _options;

    public ConfigurationCredentialStore(IOptions<HeadlessOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Credentials GetCredentials() =>
        new(_options.Account ?? string.Empty, _options.Password ?? string.Empty);
}
