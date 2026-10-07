namespace Sheddueller.Runtime;

using Microsoft.Extensions.DependencyInjection;

internal static class StartupValidationRegistration
{
    public static void TryAdd(IServiceCollection services, ServiceDescriptor hostedValidation)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ShedduellerHostedStartupValidationMarker)))
        {
            return;
        }

        services.AddSingleton<ShedduellerHostedStartupValidationMarker>();
        for (var index = 0; index < services.Count; index++)
        {
            if (services[index].ServiceType == hostedValidation.ServiceType)
            {
                services.Insert(index, hostedValidation);
                return;
            }
        }

        services.Add(hostedValidation);
    }
}
