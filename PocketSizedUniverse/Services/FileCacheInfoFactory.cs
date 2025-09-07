using Microsoft.Extensions.Logging;
using PocketSizedUniverse.API.Dto.CharaData;
using PocketSizedUniverse.API.Dto.Files;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.Services.CharaData.Models;
using PocketSizedUniverse.WebAPI;
using Serilog.Core;

namespace PocketSizedUniverse.Services
{
    public class FileCacheInfoFactory
    {
        private readonly ApiController _apiController;
        private readonly BitTorrentService _bitTorrentService;
        private readonly IpcCallerPenumbra _ipcCallerPenumbra;
        private readonly ILogger<FileCacheInfoFactory> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public FileCacheInfoFactory(ApiController apiController, BitTorrentService bitTorrentService, IpcCallerPenumbra ipcCallerPenumbra, ILogger<FileCacheInfoFactory> logger, ILoggerFactory loggerFactory)
        {
            _apiController = apiController;
            _bitTorrentService = bitTorrentService;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public FileCacheInfo CreateFromPath(string path, string gamePath)
        {
            return new FileCacheInfo(path, gamePath, _bitTorrentService, _apiController, _ipcCallerPenumbra, _loggerFactory.CreateLogger<FileCacheInfo>());
        }

        public FileCacheInfo CreateFromTorrentFileEntry(TorrentFileEntry torrentFileEntry)
        {
            return new FileCacheInfo(torrentFileEntry.TorrentFile, _bitTorrentService, _apiController, _ipcCallerPenumbra, _loggerFactory.CreateLogger<FileCacheInfo>());
        }

        public FileCacheInfo CreateFromTorrentFileDto(TorrentFileDto torrentFileDto)
        {
            return new FileCacheInfo(torrentFileDto, _bitTorrentService, _apiController, _ipcCallerPenumbra, _loggerFactory.CreateLogger<FileCacheInfo>());
        }
    }
}
