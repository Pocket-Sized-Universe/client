using Dalamud.Game.ClientState.Objects;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PocketSizedUniverse.Interop;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.MareConfiguration;
using PocketSizedUniverse.MareConfiguration.Configurations;
using PocketSizedUniverse.PlayerData.Factories;
using PocketSizedUniverse.PlayerData.Pairs;
using PocketSizedUniverse.PlayerData.Services;
using PocketSizedUniverse.Services;
using PocketSizedUniverse.Services.Events;
using PocketSizedUniverse.Services.Mediator;
using PocketSizedUniverse.Services.ServerConfiguration;
using PocketSizedUniverse.UI;
using PocketSizedUniverse.UI.Components;
using PocketSizedUniverse.UI.Components.Popup;
using PocketSizedUniverse.UI.Handlers;
using PocketSizedUniverse.WebAPI;
using PocketSizedUniverse.WebAPI.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;
using System.Net.Http.Headers;
using System.Reflection;
using PocketSizedUniverse.Services.CharaData;
using Dalamud.Game;

namespace PocketSizedUniverse;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IHost _host;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IDataManager gameData,
        IFramework framework, IObjectTable objectTable, IClientState clientState, ICondition condition, IChatGui chatGui,
        IGameGui gameGui, IDtrBar dtrBar, IPluginLog pluginLog, ITargetManager targetManager, INotificationManager notificationManager,
        ITextureProvider textureProvider, IContextMenu contextMenu, IGameInteropProvider gameInteropProvider, IGameConfig gameConfig,
        ISigScanner sigScanner)
    {
        if (!Directory.Exists(pluginInterface.ConfigDirectory.FullName))
            Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        var traceDir = Path.Join(pluginInterface.ConfigDirectory.FullName, "tracelog");
        if (!Directory.Exists(traceDir))
            Directory.CreateDirectory(traceDir);

        foreach (var file in Directory.EnumerateFiles(traceDir)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(9))
        {
            int attempts = 0;
            bool deleted = false;
            while (!deleted && attempts < 5)
            {
                try
                {
                    file.Delete();
                    deleted = true;
                }
                catch
                {
                    attempts++;
                    Thread.Sleep(500);
                }
            }
        }

        _host = new HostBuilder()
        .UseContentRoot(pluginInterface.ConfigDirectory.FullName)
        .ConfigureLogging(lb =>
        {
            lb.ClearProviders();
            lb.AddDalamudLogging(pluginLog, gameData.HasModifiedGameDataFiles);
            lb.AddFile(Path.Combine(traceDir, $"mare-trace-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), (opt) =>
            {
                opt.Append = true;
                opt.RollingFilesConvention = FileLoggerOptions.FileRollingConvention.Ascending;
                opt.MinLevel = LogLevel.Trace;
                opt.FileSizeLimitBytes = 50 * 1024 * 1024;
            });
            lb.SetMinimumLevel(LogLevel.Trace);
        })
        .ConfigureServices(collection =>
        {
            collection.AddSingleton(new WindowSystem("PocketSizedUniverse"));
            collection.AddSingleton<FileDialogManager>();
            collection.AddSingleton(new Dalamud.Localization("PocketSizedUniverse.Localization.", "", useEmbedded: true));

            collection.AddSingleton(pluginInterface);
            collection.AddSingleton(commandManager);
            collection.AddSingleton(gameData);
            collection.AddSingleton(framework);
            collection.AddSingleton(objectTable);
            collection.AddSingleton(clientState);
            collection.AddSingleton(condition);
            collection.AddSingleton(chatGui);
            collection.AddSingleton(gameGui);
            collection.AddSingleton(dtrBar);
            collection.AddSingleton(targetManager);
            collection.AddSingleton(notificationManager);
            collection.AddSingleton(textureProvider);
            collection.AddSingleton(contextMenu);
            collection.AddSingleton(gameInteropProvider);
            collection.AddSingleton(gameConfig);
            collection.AddSingleton(sigScanner);
            collection.AddSingleton(pluginInterface.UiBuilder);

            // add mare related singletons
            collection.AddSingleton<MareMediator>();
            collection.AddSingleton<ServerConfigurationManager>();
            collection.AddSingleton<ApiController>();
            collection.AddSingleton<PerformanceCollectorService>();
            collection.AddSingleton<HubFactory>();
            collection.AddSingleton<MarePlugin>();
            collection.AddSingleton<MareProfileManager>();
            collection.AddSingleton<GameObjectHandlerFactory>();
            collection.AddSingleton<PairHandlerFactory>();
            collection.AddSingleton<PairFactory>();
            collection.AddSingleton<XivDataAnalyzer>();
            collection.AddSingleton<CharacterAnalyzer>();
            collection.AddSingleton<TokenProvider>();
            collection.AddSingleton<PluginWarningNotificationService>();
            collection.AddSingleton<TagHandler>();
            collection.AddSingleton<IdDisplayHandler>();

            collection.AddSingleton<CharaDataManager>();
            collection.AddSingleton<CharaDataFileHandler>();
            collection.AddSingleton<CharaDataCharacterHandler>();
            collection.AddSingleton<CharaDataNearbyManager>();
            collection.AddSingleton<CharaDataGposeTogetherManager>();

            collection.AddSingleton<NotificationService>();
            collection.AddSingleton<VfxSpawnManager>();
            collection.AddSingleton<BlockedCharacterHandler>();
            collection.AddSingleton<IpcProvider>();
            collection.AddSingleton<SelectPairForTagUi>();
            collection.AddSingleton<EventAggregator>();
            collection.AddSingleton<DalamudUtilService>();
            collection.AddSingleton<DtrEntry>();
            collection.AddSingleton<PairManager>();
            collection.AddSingleton<RedrawManager>();
            collection.AddSingleton<IpcCallerPenumbra>();
            collection.AddSingleton<IpcCallerGlamourer>();
            collection.AddSingleton<IpcCallerCustomize>();
            collection.AddSingleton<IpcCallerHeels>();
            collection.AddSingleton<IpcCallerHonorific>();
            collection.AddSingleton<IpcCallerMoodles>();
            collection.AddSingleton<IpcCallerPetNames>();
            collection.AddSingleton<IpcCallerBrio>();
            collection.AddSingleton<IpcManager>();
            collection.AddSingleton((s) =>
            {
                var httpClient = new HttpClient();
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PocketSizedUniverse", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
                return httpClient;
            });
            collection.AddSingleton((s) => new MareConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new NotesConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerTagConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new TransientConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new XivDataStorageService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new PlayerPerformanceConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new CharaDataConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<MareConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<ServerConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<NotesConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<ServerTagConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<TransientConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<XivDataStorageService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<PlayerPerformanceConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<CharaDataConfigService>());
            collection.AddSingleton<ConfigurationMigrator>();
            collection.AddSingleton<ConfigurationSaveService>();
            collection.AddSingleton<BitTorrentService>();

            collection.AddSingleton<HubFactory>();

            // add scoped services
            collection.AddScoped<DrawEntityFactory>();
            collection.AddScoped<UiFactory>();
            collection.AddScoped<SelectTagForPairUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, SettingsUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, CompactUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, IntroUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, DownloadUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, PopoutProfileUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, DataAnalysisUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, JoinSyncshellUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, CreateSyncshellUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, EventViewerUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, CharaDataHubUi>();

            collection.AddScoped<WindowMediatorSubscriberBase, EditProfileUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, PopupHandler>();
            collection.AddScoped<IPopupHandler, BanUserPopupHandler>();
            collection.AddScoped<IPopupHandler, CensusPopupHandler>();
            collection.AddScoped<CacheCreationService>();
            collection.AddScoped<PlayerDataFactory>();
            collection.AddScoped<VisibleUserDataDistributor>();
            collection.AddScoped<UiService>();
            collection.AddScoped<CommandManagerService>();
            collection.AddScoped<UiSharedService>();
            collection.AddScoped<FileCacheInfoFactory>();

            collection.AddHostedService(p => p.GetRequiredService<ConfigurationSaveService>());
            collection.AddHostedService(p => p.GetRequiredService<MareMediator>());
            collection.AddHostedService(p => p.GetRequiredService<NotificationService>());
            collection.AddHostedService(p => p.GetRequiredService<ConfigurationMigrator>());
            collection.AddHostedService(p => p.GetRequiredService<DalamudUtilService>());
            collection.AddHostedService(p => p.GetRequiredService<PerformanceCollectorService>());
            collection.AddHostedService(p => p.GetRequiredService<DtrEntry>());
            collection.AddHostedService(p => p.GetRequiredService<EventAggregator>());
            collection.AddHostedService(p => p.GetRequiredService<IpcProvider>());
            collection.AddHostedService(p => p.GetRequiredService<MarePlugin>());
            collection.AddHostedService(p => p.GetRequiredService<BitTorrentService>());
        })
        .Build();

        _ = _host.StartAsync();
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
    }
}