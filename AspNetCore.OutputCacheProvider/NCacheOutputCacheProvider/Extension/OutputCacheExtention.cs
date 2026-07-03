using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;


namespace NCache.OSS.AspNetCore.OutputCaching;

/// <summary>
/// Extension methods for the Alachisoft NCache OutputCaching provider.
/// </summary>
public static class OutputCacheExtention
{
    /// <summary>
    /// Add output caching services and configure the related options. 
    /// Both options are required to be configured. 
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> for adding services.</param>
    /// <param name="ncacheOptions">A delegate to configure the <see cref="NCacheOutputCacheOptions"/>.</param>
    /// <returns></returns>
    public static IServiceCollection AddNCacheOutputCacheProvider(this IServiceCollection services, Action<NCacheOutputCacheOptions> ncacheOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ncacheOptions);

        services.Configure(ncacheOptions);

        services.AddSingleton<IOutputCacheStore>(sp =>
        {
            var middlewareOptions = sp.GetRequiredService<IOptions<NCacheOutputCacheOptions>>().Value;        
            //This policy will be called everytime below cases
            //1. User gives sql query using the decorate attribute.
            //2. User set the Sql query using extention function of builder. 
            //middlewareOptions?.Value?.AddBasePolicy(new DatabaseDependencyPolicy());
    
            return new NCacheOutputCacheStore(middlewareOptions);
        });
        return services;
    }

    /// <summary>
    /// Add output caching services and configure the related options. 
    /// Both options are required to be configured. 
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> for adding services.</param>
    /// <param name="configSection">A delegate to configure the <see cref="IConfiguration"/>.</param>
    /// <returns></returns>
    public static IServiceCollection AddNCacheOutputCacheProvider(this IServiceCollection services, IConfiguration configSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configSection);

        services.Configure<NCacheOutputCacheOptions>(configSection);

        services.AddSingleton<IOutputCacheStore>(sp =>
        {
            var middlewareOptions = sp.GetRequiredService<IOptions<NCacheOutputCacheOptions>>().Value;
            //This policy will be called everytime below cases
            //1. User gives sql query using the decorate attribute.
            //2. User set the Sql query using extention function of builder. 
            //middlewareOptions?.Value?.AddBasePolicy(new DatabaseDependencyPolicy());


            return new NCacheOutputCacheStore(middlewareOptions);
        });
        return services;
    }
}
