using System.Net.Http.Headers;
using Azure.Core;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Attaches a managed identity bearer token to outbound requests, so no keys or
/// connection strings are needed for Azure-to-Azure calls.
/// </summary>
public sealed class ManagedIdentityAuthHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string _scope;
    private AccessToken _token;

    public ManagedIdentityAuthHandler(TokenCredential credential, string scope)
    {
        _credential = credential;
        _scope = scope;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            _token = await _credential
                .GetTokenAsync(new TokenRequestContext([_scope]), cancellationToken)
                .ConfigureAwait(false);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
