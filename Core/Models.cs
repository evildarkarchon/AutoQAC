using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoQAC.Core.Models;

/// <summary>
/// Core information and settings for PACT operations
/// </summary>
public class PactInfo
{
    public string XEditExecutable { get; set; } = string.Empty;
    public string XEditPath { get; set; } = string.Empty;
    public string LoadOrderTxt { get; set; } = string.Empty;
    public string LoadOrderPath { get; set; } = string.Empty;
    public int JournalExpiration { get; set; } = 7;
    public int CleaningTimeout { get; set; } = 300;

    public HashSet<string> CleanResultsUDR { get; } = new();
    public HashSet<string> CleanResultsITM { get; } = new();
    public HashSet<string> CleanResultsNVM { get; } = new();
    public HashSet<string> CleanResultsPartialForms { get; } = new();
    public HashSet<string> CleanFailedList { get; } = new();
    public int PluginsProcessed { get; set; }
    public int PluginsCleaned { get; set; }

    public List<string> LclSkipList { get; } = new();

    // Game-specific skip lists with default entries
    public List<string> Fo3SkipList { get; } = new()
    {
        "Fallout3.esm",
        "Anchorage.esm",
        "ThePitt.esm",
        "BrokenSteel.esm",
        "PointLookout.esm",
        "Zeta.esm"
    };

    public List<string> FnvSkipList { get; } = new()
    {
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
    };

    public List<string> Fo4SkipList { get; } = new()
    {
        "Fallout4.esm",
        "DLCRobot.esm",
        "DLCworkshop01.esm",
        "DLCworkshop02.esm",
        "DLCworkshop03.esm",
        "DLCCoast.esm",
        "DLCNukaWorld.esm",
        "DLCUltraHighResolution.esm"
    };

    public List<string> SseSkipList { get; } = new()
    {
        "Skyrim.esm",
        "Update.esm",
        "Dawnguard.esm",
        "HearthFires.esm",
        "Dragonborn.esm"
    };

    public IEnumerable<string> VipSkipList =>
        Fo3SkipList.Concat(FnvSkipList)
                   .Concat(Fo4SkipList)
                   .Concat(SseSkipList)
                   .ToList();

    public string XEditLogTxt { get; set; } = string.Empty;
    public string XEditExcLog { get; set; } = string.Empty;

    // Game-specific XEdit executables
    public IReadOnlySet<string> XEditListFallout3 { get; } = new HashSet<string>
    {
        "FO3Edit.exe",
        "FO3EditQuickAutoClean.exe"
    };

    public IReadOnlySet<string> XEditListNewVegas { get; } = new HashSet<string>
    {
        "FNVEdit.exe",
        "FNVEditQuickAutoClean.exe"
    };

    public IReadOnlySet<string> XEditListFallout4 { get; } = new HashSet<string>
    {
        "FO4Edit.exe",
        "FO4EditQuickAutoClean.exe",
        "FO4VREdit.exe",
        "FO4VREditQuickAutoClean.exe"
    };

    public IReadOnlySet<string> XEditListSkyrimSE { get; } = new HashSet<string>
    {
        "SSEEdit.exe",
        "SSEEditQuickAutoClean.exe",
        "SkyrimVREdit.exe",
        "SkyrimVREditQuickAutoClean.exe"
    };

    public IReadOnlySet<string> XEditListUniversal { get; } = new HashSet<string>
    {
        "xEdit.exe",
        "xEditQuickAutoClean.exe"
    };

    // Computed properties for lowercase comparisons
    public IReadOnlySet<string> LowerSpecific =>
        XEditListFallout3.Concat(XEditListNewVegas)
                         .Concat(XEditListFallout4)
                         .Concat(XEditListSkyrimSE)
                         .Select(x => x.ToLowerInvariant())
                         .ToHashSet();

    public IReadOnlySet<string> LowerUniversal =>
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
    FalloutNV,
    Fallout4,
    SkyrimSE
}

/// <summary>
/// Extension methods for GameMode enum
/// </summary>
public static class GameModeExtensions
{
    public static string ToMasterFile(this GameMode mode) => mode switch
    {
        GameMode.Fallout3 => "Fallout3.esm",
        GameMode.FalloutNV => "FalloutNV.esm",
        GameMode.Fallout4 => "Fallout4.esm",
        GameMode.SkyrimSE => "Skyrim.esm",
        _ => throw new ArgumentException($"Unknown game mode: {mode}")
    };

    public static string ToShortName(this GameMode mode) => mode switch
    {
        GameMode.Fallout3 => "fo3",
        GameMode.FalloutNV => "fnv",
        GameMode.Fallout4 => "fo4",
        GameMode.SkyrimSE => "sse",
        _ => throw new ArgumentException($"Unknown game mode: {mode}")
    };
}