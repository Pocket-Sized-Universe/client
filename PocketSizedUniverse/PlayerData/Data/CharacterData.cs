using PocketSizedUniverse.API.Data;

using PocketSizedUniverse.API.Data.Enum;
using PocketSizedUniverse.API.Dto.CharaData;

namespace PocketSizedUniverse.PlayerData.Data;

public class CharacterData
{
    public Dictionary<ObjectKind, string> CustomizePlusScale { get; set; } = [];
    public Dictionary<ObjectKind, List<FileRedirectEntry>> FileReplacements { get; set; } = [];
    public Dictionary<ObjectKind, List<TorrentFileEntry>> FileSwaps { get; set; } = [];
    public Dictionary<ObjectKind, string> GlamourerString { get; set; } = [];
    public string HeelsData { get; set; } = string.Empty;
    public string HonorificData { get; set; } = string.Empty;
    public string ManipulationString { get; set; } = string.Empty;
    public string MoodlesData { get; set; } = string.Empty;
    public string PetNamesData { get; set; } = string.Empty;

    public void SetFragment(ObjectKind kind, CharacterDataFragment? fragment)
    {
        if (kind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer);
            HeelsData = playerFragment?.HeelsData ?? string.Empty;
            HonorificData = playerFragment?.HonorificData ?? string.Empty;
            ManipulationString = playerFragment?.ManipulationString ?? string.Empty;
            MoodlesData = playerFragment?.MoodlesData ?? string.Empty;
            PetNamesData = playerFragment?.PetNamesData ?? string.Empty;
        }

        if (fragment is null)
        {
            CustomizePlusScale.Remove(kind);
            FileReplacements.Remove(kind);
            FileSwaps.Remove(kind);
            GlamourerString.Remove(kind);
        }
        else
        {
            CustomizePlusScale[kind] = fragment.CustomizePlusScale;
            FileReplacements[kind] = fragment.FileReplacements;
            FileSwaps[kind] = fragment.FileSwaps;
            GlamourerString[kind] = fragment.GlamourerString;
        }
    }

    public API.Data.CharacterData ToAPI()
    {
        return new API.Data.CharacterData()
        {
            FileReplacements = FileReplacements,
            FileSwaps = FileSwaps,
            GlamourerData = GlamourerString.ToDictionary(d => d.Key, d => d.Value),
            ManipulationData = ManipulationString,
            HeelsData = HeelsData,
            CustomizePlusData = CustomizePlusScale.ToDictionary(d => d.Key, d => d.Value),
            HonorificData = HonorificData,
            MoodlesData = MoodlesData,
            PetNamesData = PetNamesData
        };
    }
}