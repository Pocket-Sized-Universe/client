using PocketSizedUniverse.Services.CharaData.Models;
using PocketSizedUniverse.WebAPI;
using System.Collections.Concurrent;

namespace PocketSizedUniverse.Services
{
    public class FileCacheInfoFactory
    {
        private readonly ApiController _apiController;
        private readonly BitTorrentService _bitTorrentService;
        private readonly ConcurrentDictionary<string, FileCacheInfo> _cache = new();

        public FileCacheInfoFactory(ApiController apiController, BitTorrentService bitTorrentService)
        {
            _apiController = apiController;
            _bitTorrentService = bitTorrentService;
        }

        public FileCacheInfo CreateFromHash(string hash)
        {
            return _cache.GetOrAdd(hash, h => new FileCacheInfo(h, _bitTorrentService, _apiController));
        }
    }
}
