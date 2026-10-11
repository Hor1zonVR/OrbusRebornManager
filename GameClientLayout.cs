using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OrbusRebornManager;

/// <summary>
/// Known PC Unity player layouts for the Community Edition and its Renewed
/// client fork. No arbitrary Unity executable is accepted: these are explicit
/// Orbus client names, always paired with GameAssembly.dll and matching _Data.
/// </summary>
public static class GameClientLayout
{
    private static readonly string[] ExecutableNames =
    {
        "vrclient.exe",
        // Confirmed in FourOfSpades4/reborn-server v0.6.1-alpha:
        // New-Client/OrbusVR.exe + OrbusVR_Data/ + GameAssembly.dll.
        "OrbusVR.exe",
        "OrbusVRRenewed.exe",
        "OrbusVR Renewed.exe",
        "OrbusRenewed.exe"
    };

    public static string NormalizeFolder(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            throw new InvalidOperationException(
                "Select a folder containing an OrbusVR Reborn or Renewed PC client.");

        string path = selection.Trim().Trim('"');

        // Permit selecting the main executable directly as well as its folder.
        if (File.Exists(path))
        {
            if (!ExecutableNames.Any(name =>
                name.Equals(Path.GetFileName(path),
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Select the OrbusVR client executable or the folder containing it.");

            path = Path.GetDirectoryName(path)!;
        }

        if (!Directory.Exists(path))
            throw new InvalidOperationException(
                "Choose the extracted OrbusVR Reborn or Renewed game folder.");

        path = Path.GetFullPath(path);

        // Renewed v0.6.1-alpha wraps the client in New-Client/ within its
        // archive. Accept choosing either that folder or the outer extracted
        // folder, but never recurse through arbitrary subdirectories.
        string nested = Path.Combine(path, "New-Client");
        if (!File.Exists(Path.Combine(path, "GameAssembly.dll")) &&
            Directory.Exists(nested) &&
            File.Exists(Path.Combine(nested, "GameAssembly.dll")) &&
            File.Exists(Path.Combine(nested, "OrbusVR.exe")) &&
            Directory.Exists(Path.Combine(nested, "OrbusVR_Data")))
        {
            path = nested;
        }

        FindExecutable(path);
        return path;
    }

    public static string FindExecutable(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "GameAssembly.dll")))
            throw new InvalidOperationException(
                "This is not a supported PC client folder: GameAssembly.dll is missing.");

        foreach (string name in ExecutableNames)
        {
            string file = Path.Combine(folder, name);
            string data = Path.Combine(folder,
                Path.GetFileNameWithoutExtension(name) + "_Data");

            if (File.Exists(file) && Directory.Exists(data))
                return file;
        }

        throw new InvalidOperationException(
            "Couldn't find a recognised OrbusVR Reborn / Renewed executable " +
            "with its matching _Data folder. Select the extracted PC game folder, " +
            "not the server files or the ZIP itself.");
    }

    public static bool IsClientRunning()
    {
        try
        {
            return ExecutableNames.Any(name =>
                Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(name)).Length > 0);
        }
        catch
        {
            // Match the previous behaviour when process enumeration fails.
            return false;
        }
    }
}
