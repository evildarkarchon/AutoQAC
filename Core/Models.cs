using System.IO;

namespace PACT.Core;

/// <summary>
/// Core information and settings for PACT operations
/// </summary>
public class PactInfo
{
    public string XEditExecutable { get; private set; } = string.Empty;
    public string XEditPath { get; private set; } = string.Empty;
    public string LoadOrderTxt { get; private set; } = string.Empty;
    public string LoadOrderPath { get; private set; } = string.Empty;
    public int JournalExpiration { get; set; } = 7;
    public int CleaningTimeout { get; set; } = 300;

    public HashSet<string> CleanResultsUdr { get; } = [];
    public HashSet<string> CleanResultsItm { get; } = [];
    public HashSet<string> CleanResultsNvm { get; } = [];
    public HashSet<string> CleanResultsPartialForms { get; } = [];
    public HashSet<string> CleanFailedList { get; } = [];
    public int PluginsProcessed { get; set; }
    public int PluginsCleaned { get; set; }

    public List<string> LclSkipList { get; } = [];

    // Game-specific skip lists with default entries
    private List<string> Fo3SkipList { get; } =
    [
        "Fallout3.esm",
        "Anchorage.esm",
        "ThePitt.esm",
        "BrokenSteel.esm",
        "PointLookout.esm",
        "Zeta.esm"
    ];

    private List<string> FnvSkipList { get; } =
    [
        "FalloutNV.esm",
        "DeadMoney.esm",
        "HonestHearts.esm",
        "OldWorldBlues.esm",
        "LonesomeRoad.esm",
        "GunRunnersArsenal.esm",
        "CaravanPack.esm",
        "ClassicPack.esm",
        "MercenaryPack.esm",
        "TribalPack.esm"
    ];

    private List<string> Fo4SkipList { get; } =
    [
        "Fallout4.esm",
        "DLCRobot.esm",
        "DLCworkshop01.esm",
        "DLCworkshop02.esm",
        "DLCworkshop03.esm",
        "DLCCoast.esm",
        "DLCNukaWorld.esm",
        "DLCUltraHighResolution.esm"
    ];

    private List<string> SseSkipList { get; } =
    [
        "Skyrim.esm",
        "Update.esm",
        "Dawnguard.esm",
        "HearthFires.esm",
        "Dragonborn.esm"
    ];

    private IEnumerable<string> AggregateSkipLists()
    {
        return Fo3SkipList.Concat(FnvSkipList)
            .Concat(Fo4SkipList)
            .Concat(SseSkipList);
    }

    public IEnumerable<string> GetVipSkipList() => AggregateSkipLists();

    public string XEditLogTxt { get; set; } = string.Empty;
    public string XEditExcLog { get; set; } = string.Empty;

    // Game-specific XEdit executables
    private IReadOnlySet<string> XEditListFallout3 { get; } = new HashSet<string>
    {
        "FO3Edit.exe",
        "FO3EditQuickAutoClean.exe"
    };

    private IReadOnlySet<string> XEditListNewVegas { get; } = new HashSet<string>
    {
        "FNVEdit.exe",
        "FNVEditQuickAutoClean.exe"
    };

    private IReadOnlySet<string> XEditListFallout4 { get; } = new HashSet<string>
    {
        "FO4Edit.exe",
        "FO4EditQuickAutoClean.exe",
        "FO4VREdit.exe",
        "FO4VREditQuickAutoClean.exe"
    };

    private IReadOnlySet<string> XEditListSkyrimSe { get; } = new HashSet<string>
    {
        "SSEEdit.exe",
        "SSEEditQuickAutoClean.exe",
        "SkyrimVREdit.exe",
        "SkyrimVREditQuickAutoClean.exe"
    };

    private IReadOnlySet<string> XEditListUniversal { get; } = new HashSet<string>
    {
        "xEdit.exe",
        "xEditQuickAutoClean.exe"
    };

    // Computed properties for lowercase comparisons
    private IReadOnlySet<string> LowerSpecific =>
        XEditListFallout3.Concat(XEditListNewVegas)
                         .Concat(XEditListFallout4)
                         .Concat(XEditListSkyrimSe)
                         .Select(x => x.ToLowerInvariant())
                         .ToHashSet();

    private IReadOnlySet<string> LowerUniversal =>
        XEditListUniversal.Select(x => x.ToLowerInvariant()).ToHashSet();

    public void UpdateXEditPaths(string xEditPath)
    {
        if (string.IsNullOrEmpty(xEditPath))
        {
            XEditExecutable = string.Empty;
            XEditPath = string.Empty;
            return;
        }

        XEditPath = xEditPath;

        if (Path.HasExtension(xEditPath) && Path.GetExtension(xEditPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            XEditExecutable = Path.GetFileName(xEditPath);
        }
        else if (Directory.Exists(xEditPath))
        {
            var exeFile = Directory.GetFiles(xEditPath, "*.exe")
                                 .FirstOrDefault(f => IsXEdit(Path.GetFileName(f)));

            if (exeFile != null)
            {
                XEditPath = exeFile;
                XEditExecutable = Path.GetFileName(exeFile);
            }
        }
    }

    public void UpdateLoadOrderPath(string loadOrderPath)
    {
        LoadOrderPath = loadOrderPath;
        LoadOrderTxt = !string.IsNullOrEmpty(loadOrderPath) ? Path.GetFileName(loadOrderPath) : string.Empty;
    }

    public bool IsXEdit(string filename)
    {
        var lowerFilename = filename.ToLowerInvariant();
        return LowerSpecific.Contains(lowerFilename) || LowerUniversal.Contains(lowerFilename);
    }
}

/// <summary>
/// Represents different game modes supported by PACT
/// </summary>
public enum GameMode
{
    Fallout3,
    FalloutNv,
    Fallout4,
    SkyrimSe
}

/// <summary>
/// Extension methods for GameMode enum
/// </summary>
public static class GameModeExtensions
{
    public static string ToMasterFile(this GameMode mode) => mode switch
    {
        GameMode.Fallout3 => "Fallout3.esm",
        GameMode.FalloutNv => "FalloutNV.esm",
        GameMode.Fallout4 => "Fallout4.esm",
        GameMode.SkyrimSe => "Skyrim.esm",
        _ => throw new ArgumentException($"Unknown game mode: {mode}")
    };

    public static string ToShortName(this GameMode mode) => mode switch
    {
        GameMode.Fallout3 => "fo3",
        GameMode.FalloutNv => "fnv",
        GameMode.Fallout4 => "fo4",
        GameMode.SkyrimSe => "sse",
        _ => throw new ArgumentException($"Unknown game mode: {mode}")
    };
}