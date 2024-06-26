using System.Diagnostics;
using System.Text.Json;
using MaskinportenAuthentication.Exceptions;
using MaskinportenAuthentication.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MaskinportenAuthentication;

/// <inheritdoc/>
public sealed class MaskinportenClient : IMaskinportenClient
{
    /// <summary>
    /// The margin to take into consideration when determining if a token has expired (seconds).
    /// <remarks>This value represents the worst-case latency scenario for <em>outbound</em> connections carrying the access token.</remarks>
    /// </summary>
    internal const int _tokenExpirationMargin = 30;

    private readonly ILogger<MaskinportenClient>? _logger;
    private readonly IOptionsMonitor<MaskinportenSettings> _options;
    private readonly MemoryCache _tokenCache;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Instantiates a new <see cref="MaskinportenClient"/> object.
    /// </summary>
    /// <param name="options">Maskinporten settings.</param>
    /// <param name="httpClientFactory">HttpClient factory.</param>
    /// <param name="logger">Optional logger interface.</param>
    public MaskinportenClient(
        IOptionsMonitor<MaskinportenSettings> options,
        IHttpClientFactory httpClientFactory,
        ILogger<MaskinportenClient>? logger = default
    )
    {
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _tokenCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
    }

    /// <inheritdoc/>
    public Task<MaskinportenTokenResponse> GetAccessToken(
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default
    )
    {
        string formattedScopes = FormattedScopes(scopes);

        var result = _tokenCache.GetOrCreate<object>(
            formattedScopes,
            entry =>
            {
                entry.SetSize(1);
                return new Lazy<Task<MaskinportenTokenResponse>>(
                    () => CacheEntryFactory(formattedScopes, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication
                );
            }
        );

        Debug.Assert(result is MaskinportenTokenResponse or Lazy<Task<MaskinportenTokenResponse>>);
        if (result is Lazy<Task<MaskinportenTokenResponse>> lazy)
        {
            _logger?.LogDebug("Waiting for token request to resolve with Maskinporten");
            return lazy.Value;
        }

        _logger?.LogDebug(
            "Using cached access token which expires at {ExpiresAt}",
            ((MaskinportenTokenResponse)result).ExpiresAt
        );
        return Task.FromResult((MaskinportenTokenResponse)result);
    }

    /// <summary>
    /// Factory method that returns a task which will send request a token from Maskinporten, then insert this token
    /// in the <see cref="_tokenCache"/> before returning it to the caller.
    /// </summary>
    /// <param name="formattedScopes">A single space-separated string containing the scopes to authorize for.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <exception cref="MaskinportenTokenExpiredException">The token received from Maskinporten has already expired.</exception>
    private Task<MaskinportenTokenResponse> CacheEntryFactory(
        string formattedScopes,
        CancellationToken cancellationToken = default
    )
    {
        return Task.Run(
            async () =>
            {
                var localTime = DateTime.UtcNow;
                MaskinportenTokenResponse token = await HandleMaskinportenAuthentication(
                    formattedScopes,
                    cancellationToken
                );

                var cacheExpiry = localTime.AddSeconds(token.ExpiresIn - _tokenExpirationMargin);
                if (cacheExpiry <= DateTime.UtcNow)
                {
                    _tokenCache.Remove(formattedScopes);
                    throw new MaskinportenTokenExpiredException(
                        $"Access token cannot be used because it has a calculated expiration in the past (taking into account a margin of {_tokenExpirationMargin} seconds): {token}"
                    );
                }

                return _tokenCache.Set(
                    formattedScopes,
                    token,
                    new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(cacheExpiry)
                );
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Handles the sending of grant requests to Maskinporten
    /// </summary>
    /// <param name="formattedScopes">A single space-separated string containing the scopes to authorize for.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns><inheritdoc cref="GetAccessToken"/></returns>
    /// <exception cref="MaskinportenAuthenticationException"><inheritdoc cref="GetAccessToken"/></exception>
    private async Task<MaskinportenTokenResponse> HandleMaskinportenAuthentication(
        string formattedScopes,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            string jwt = GenerateJwtGrant(formattedScopes);
            FormUrlEncodedContent payload = GenerateAuthenticationPayload(jwt);

            _logger?.LogDebug(
                "Sending grant request to Maskinporten: {GrantRequest}",
                await payload.ReadAsStringAsync(cancellationToken)
            );

            string tokenAuthority = _options.CurrentValue.Authority.Trim('/');
            HttpClient client = _httpClientFactory.CreateClient();
            using HttpResponseMessage response = await client.PostAsync(
                $"{tokenAuthority}/token",
                payload,
                cancellationToken
            );
            MaskinportenTokenResponse token =
                await ParseServerResponse(response, cancellationToken)
                ?? throw new MaskinportenAuthenticationException("Invalid response from Maskinporten");

            _logger?.LogDebug("Token retrieved successfully");
            return token;
        }
        catch (MaskinportenException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new MaskinportenAuthenticationException($"Authentication with Maskinporten failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Generates a JWT grant for the supplied scope claims along with the pre-configured client id and private key.
    /// </summary>
    /// <param name="formattedScopes">A space-separated list of scopes to make a claim for.</param>
    /// <returns><inheritdoc cref="JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)"/></returns>
    /// <exception cref="MaskinportenConfigurationException"></exception>
    internal string GenerateJwtGrant(string formattedScopes)
    {
        MaskinportenSettings? settings;
        try
        {
            settings = _options.CurrentValue;
        }
        catch (OptionsValidationException e)
        {
            throw new MaskinportenConfigurationException(
                $"Error reading MaskinportenSettings from the current app configuration",
                e
            );
        }

        var now = DateTime.UtcNow;
        var expiry = now.AddMinutes(2);
        var jwtDescriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.ClientId,
            Audience = settings.Authority,
            IssuedAt = now,
            Expires = expiry,
            SigningCredentials = new SigningCredentials(settings.Key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["scope"] = formattedScopes, ["jti"] = Guid.NewGuid().ToString() }
        };

        return new JsonWebTokenHandler().CreateToken(jwtDescriptor);
    }

    /// <summary>
    /// <para>
    /// Generates an authentication payload from the supplied JWT (see <see cref="GenerateJwtGrant"/>).
    /// </para>
    /// <para>
    /// This payload needs to be a <see cref="FormUrlEncodedContent"/> object with some precise parameters,
    /// as per <a href="https://docs.digdir.no/docs/Maskinporten/maskinporten_guide_apikonsument#5-be-om-token">the docs.</a>.
    /// </para>
    /// </summary>
    /// <param name="jwtAssertion">The JWT token generated by <see cref="GenerateJwtGrant"/>.</param>
    internal static FormUrlEncodedContent GenerateAuthenticationPayload(string jwtAssertion)
    {
        return new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwtAssertion
            }
        );
    }

    /// <summary>
    /// Parses the Maskinporten server response and deserializes the JSON body.
    /// </summary>
    /// <param name="httpResponse">The server response.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A <see cref="MaskinportenTokenResponse"/> for successful requests.</returns>
    /// <exception cref="MaskinportenAuthenticationException">Authentication failed.
    /// This could be caused by an authentication/authorization issue or a myriad of tother circumstances.</exception>
    private static async Task<MaskinportenTokenResponse> ParseServerResponse(
        HttpResponseMessage httpResponse,
        CancellationToken cancellationToken = default
    )
    {
        string content = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new MaskinportenAuthenticationException(
                    $"Maskinporten authentication failed with status code {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}): {content}"
                );
            }

            return JsonSerializer.Deserialize<MaskinportenTokenResponse>(content)
                ?? throw new JsonException("JSON body is null");
        }
        catch (MaskinportenException)
        {
            throw;
        }
        catch (JsonException e)
        {
            throw new MaskinportenAuthenticationException(
                $"Maskinporten replied with invalid JSON formatting: {content}",
                e
            );
        }
        catch (Exception e)
        {
            throw new MaskinportenAuthenticationException($"Authentication with Maskinporten failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Formats a list of scopes according to the expected formatting (space-delimited).
    /// See <a href="https://docs.digdir.no/docs/Maskinporten/maskinporten_guide_apikonsument#5-be-om-token">the docs</a> for more information.
    /// </summary>
    /// <param name="scopes">A collection of scopes.</param>
    /// <returns>A single string containing the supplied scopes.</returns>
    internal static string FormattedScopes(IEnumerable<string> scopes) => string.Join(" ", scopes);
}
