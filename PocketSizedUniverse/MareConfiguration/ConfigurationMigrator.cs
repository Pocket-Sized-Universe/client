using Dalamud.Plugin;
using PocketSizedUniverse.WebAPI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PocketSizedUniverse.MareConfiguration;

public class ConfigurationMigrator(ILogger<ConfigurationMigrator> logger, TransientConfigService transientConfigService,
    ServerConfigService serverConfigService, MareConfigService mareConfigService, IDalamudPluginInterface pluginInterface) : IHostedService
{
    private readonly ILogger<ConfigurationMigrator> _logger = logger;
    private readonly MareConfigService _mareConfigService = mareConfigService;
    private readonly IDalamudPluginInterface _pluginInterface = pluginInterface;

    public void Migrate()
    {
        if (string.IsNullOrEmpty(_mareConfigService.Current.CacheFolder))
        {
            _mareConfigService.Current.CacheFolder = _pluginInterface.GetPluginConfigDirectory();
            _mareConfigService.Save();
        }

        if (transientConfigService.Current.Version == 0)
        {
            _logger.LogInformation("Migrating Transient Config V0 => V1");
            transientConfigService.Current.TransientConfigs.Clear();
            transientConfigService.Current.Version = 1;
            transientConfigService.Save();
        }

        if (serverConfigService.Current.Version == 1)
        {
            _logger.LogInformation("Migrating Server Config V1 => V2");
            var centralServer = serverConfigService.Current.ServerStorage.Find(f => f.ServerName.Equals("Lunae Crescere Incipientis (Central Server EU)", StringComparison.Ordinal));
            if (centralServer != null)
            {
                centralServer.ServerName = ApiController.MainServer;
            }
            serverConfigService.Current.Version = 2;
            serverConfigService.Save();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Migrate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
