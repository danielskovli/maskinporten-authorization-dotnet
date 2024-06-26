using MaskinportenAuthentication.Delegates;
using MaskinportenAuthentication.Exceptions;
using MaskinportenAuthentication.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MaskinportenAuthentication.Extensions;

public static class MaskinportenClientIntegration
{
    /// <summary>
    /// Expected JSON property name in appsettings.json
    /// </summary>
    private const string _appSettingsKeyName = "MaskinportenSettingsFilepath";

    /// <summary>
    /// Expected JSON property name in maskinporten-settings.json
    /// </summary>
    private const string _configSectionPath = "MaskinportenSettings";

    /// <summary>
    /// Assumed filepath to maskinporten-settings.json if no alternative path is provided via <see cref="_appSettingsKeyName"/>
    /// </summary>
    private const string _defaultSettingsFilepath = "/mnt/app-secrets/maskinporten-settings.json";

    /// <summary>
    /// <para>
    /// Adds a <see cref="MaskinportenClient"/> service and configures the required dependencies.
    /// </para>
    /// <para>
    /// Requires an `appsettings.json` entry under <see cref="_appSettingsKeyName"/> with the location to a `maskinporten-settings.json` file.
    /// In the absence of this JSON property, the system expects a file located as specified in <see cref="_defaultSettingsFilepath"/>.
    /// </para>
    /// <para>
    /// The `maskinporten-settings.json` is watched by the system and will be reloaded when changes are detected.
    /// This is extremely useful for long-running processes that may want to implement regular key rotation, etc.
    /// </para>
    /// </summary>
    /// <param name="validateSettingsLocation">Specifies whether to validate the existence of the settings file or not.</param>
    /// <exception cref="MaskinportenConfigurationException">Missing or invalid settings file.</exception>
    public static IHostApplicationBuilder AddMaskinportenClient(
        this IHostApplicationBuilder builder,
        bool validateSettingsLocation = true
    )
    {
        // Has IMaskinportenClient already been registered?
        if (builder.Services.Any(x => x.ServiceType == typeof(IMaskinportenClient)))
        {
            return builder;
        }

        string jsonProvidedPath =
            builder.Configuration.GetValue<string>(_appSettingsKeyName) ?? _defaultSettingsFilepath;
        string jsonAbsolutePath = Path.GetFullPath(jsonProvidedPath);
        string jsonDir = Path.GetDirectoryName(jsonAbsolutePath) ?? string.Empty;
        string jsonFile = Path.GetFileName(jsonAbsolutePath);

        if (validateSettingsLocation && !File.Exists(jsonAbsolutePath))
        {
            throw new MaskinportenConfigurationException(
                $"Maskinporten settings not found at specified location: {jsonAbsolutePath}"
            );
        }

        builder.Configuration.AddJsonFile(
            provider: new PhysicalFileProvider(jsonDir),
            path: jsonFile,
            optional: !validateSettingsLocation,
            reloadOnChange: true
        );
        builder
            .Services.AddOptions<MaskinportenSettings>()
            .BindConfiguration(_configSectionPath)
            .ValidateDataAnnotations();
        builder.Services.AddSingleton<IMaskinportenClient, MaskinportenClient>();

        return builder;
    }

    /// <summary>
    /// <para>
    /// Adds a <see cref="MaskinportenClient"/> service and configures the required dependencies.
    /// </para>
    /// <para>
    /// Requires a configuration object which will be static for the lifetime of the service. If you require
    /// settings to update periodically, please refer to <see cref="AddMaskinportenClient(Microsoft.Extensions.Hosting.IHostApplicationBuilder, bool)"/>
    /// </para>
    /// </summary>
    /// <param name="configureOptions">
    /// Action delegate that provides <see cref="MaskinportenSettings"/> configuration for the <see cref="MaskinportenClient"/> service
    /// </param>
    public static IServiceCollection AddMaskinportenClient(
        this IServiceCollection services,
        Action<MaskinportenSettings> configureOptions
    )
    {
        // Has IMaskinportenClient already been registered?
        if (services.Any(x => x.ServiceType == typeof(IMaskinportenClient)))
        {
            return services;
        }

        services.AddOptions<MaskinportenSettings>().Configure(configureOptions).ValidateDataAnnotations();
        services.AddSingleton<IMaskinportenClient, MaskinportenClient>();

        return services;
    }

    /// <summary>
    /// <para>
    /// Sets up a <see cref="MaskinportenDelegatingHandler"/> middleware for the supplied <see cref="HttpClient"/>,
    /// which will inject an Authorization header with a Bearer token for all requests.
    /// </para>
    /// <para>
    /// If your target API does <em>not</em> use this authentication scheme, you should consider implementing
    /// <see cref="MaskinportenClient.GetAccessToken"/> directly and handling authorization details manually.
    /// </para>
    /// </summary>
    /// <param name="scopes">One or more scopes to claim authorization for with Maskinporten</param>
    public static IHttpClientBuilder UseMaskinportenAuthorization(
        this IHttpClientBuilder builder,
        params string[] scopes
    )
    {
        return builder.AddHttpMessageHandler(provider => new MaskinportenDelegatingHandler(scopes, provider));
    }
}
