using PocketSizedUniverse.API.Dto.CharaData;

namespace PocketSizedUniverse.PlayerData.Data;

public class CharacterDataFragment
{
    public string CustomizePlusScale { get; set; } = string.Empty;
    public List<FileRedirectEntry> FileReplacements { get; set; } = [];
    public List<TorrentFileEntry> FileSwaps { get; set; } = [];
    public string GlamourerString { get; set; } = string.Empty;
}
