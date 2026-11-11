using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrAgent.Domain.Rules;

namespace PrAgent.Application;

public static class ApplicationRegistration
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<AppOptions>().BindConfiguration(AppOptions.Section);
        services.AddOptions<SapOptions>().BindConfiguration(SapOptions.Section);
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BusinessClock>();
        services.AddSingleton<RulesEngine>();
        services.AddSingleton<MasterDataCacheState>();
        services.AddScoped<MasterDataService>();
        services.AddScoped<EvaluationService>();
        services.AddScoped<RequestService>();
        services.AddScoped<PrCreationWorker>();
        services.AddScoped<AuditLog>();
        services.AddScoped<Notifier>();
        return services;
    }
}
