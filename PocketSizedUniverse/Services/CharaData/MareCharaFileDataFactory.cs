using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.FileCache;
using PocketSizedUniverse.Services.CharaData.Models;

namespace PocketSizedUniverse.Services.CharaData;

public sealed class MareCharaFileDataFactory
{
    private readonly FileCacheManager _fileCacheManager;

    public MareCharaFileDataFactory(FileCacheManager fileCacheManager)
    {
        _fileCacheManager = fileCacheManager;
    }

    public MareCharaFileData Create(string description, CharacterData characterCacheDto)
    {
        return new MareCharaFileData(_fileCacheManager, description, characterCacheDto);
    }
}