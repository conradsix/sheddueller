namespace Sheddueller.Dashboard.Internal;

internal static class DashboardAssets
{
    public static string Version
    {
        get
        {
            var assembly = typeof(ShedduellerDashboardOptions).Assembly;
            var attribute = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
              assembly,
              typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var version = attribute?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "dev";
            var location = assembly.Location;
            var stamp = string.IsNullOrEmpty(location) || !File.Exists(location)
              ? assembly.ManifestModule.ModuleVersionId.ToString("N")
              : File.GetLastWriteTimeUtc(location).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return Uri.EscapeDataString(string.Concat(version, ".", stamp));
        }
    }
}
