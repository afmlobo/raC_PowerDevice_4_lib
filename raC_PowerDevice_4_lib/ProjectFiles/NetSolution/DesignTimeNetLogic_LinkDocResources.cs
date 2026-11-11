#region Using directives
using System;
using System.IO;
using System.Text.RegularExpressions;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.SerialPort;
using FTOptix.Core;
#endregion

/// <summary>
/// Design-time NetLogic to create ResourceUri variables on raC_4_01_raC_Dvc_PM5000_PQEM_HelpDashboard
/// for every file under res/Doc/. This ensures all doc files (images, markdown, HTML, JS) are
/// referenced in the FTOptix project and get included in library exports.
///
/// Usage: Right-click this NetLogic → Execute LinkDocResources. Run once before library export.
/// </summary>
public class DesignTimeNetLogic_LinkDocResources : BaseNetLogic
{
    [ExportMethod]
    public void LinkDocResources()
    {
        try
        {
            // Resolve the project directory
            string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
            string docFolder = Path.Combine(projectDir, "res", "Doc");

            if (!Directory.Exists(docFolder))
            {
                Log.Error("DocResourceLinker", $"res/Doc folder not found at: {docFolder}");
                return;
            }

            // Get all files in res/Doc
            string[] files = Directory.GetFiles(docFolder);
            Log.Info("DocResourceLinker", $"Found {files.Length} files in res/Doc");

            int created = 0;
            int skipped = 0;

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                string varName = SanitizeNodeName(fileName);

                // Skip if variable already exists (idempotent — safe to re-run)
                if (Owner.GetVariable(varName) != null)
                {
                    skipped++;
                    continue;
                }

                // Create variable with ResourceUri data type
                var resourceVar = InformationModel.MakeVariable(varName, FTOptix.Core.DataTypes.ResourceUri);
                resourceVar.Value = ResourceUri.FromProjectRelativePath("res/Doc/" + fileName);
                Owner.Add(resourceVar);

                created++;
                Log.Info("DocResourceLinker", $"  + {varName} → %PROJECTDIR%/res/Doc/{fileName}");
            }

            Log.Info("DocResourceLinker", $"Done! Created {created} new variables, skipped {skipped} existing. Total files: {files.Length}");
        }
        catch (Exception ex)
        {
            Log.Error("DocResourceLinker", $"Error: {ex.Message}");
        }
    }

    [ExportMethod]
    public void UnlinkDocResources()
    {
        try
        {
            string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
            string docFolder = Path.Combine(projectDir, "res", "Doc");

            if (!Directory.Exists(docFolder))
            {
                Log.Warning("DocResourceLinker", "res/Doc folder not found — nothing to unlink");
                return;
            }

            string[] files = Directory.GetFiles(docFolder);
            int removed = 0;

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                string varName = SanitizeNodeName(fileName);

                var existing = Owner.GetVariable(varName);
                if (existing != null)
                {
                    Owner.Remove(existing);
                    removed++;
                }
            }

            Log.Info("DocResourceLinker", $"Removed {removed} resource variables from {Owner.BrowseName}");
        }
        catch (Exception ex)
        {
            Log.Error("DocResourceLinker", $"Error unlinking: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts a filename into a valid FTOptix node BrowseName.
    /// e.g. "image-1.png" → "image_1_png", "DASHBOARD_SCREENS_SECTION.md" → "DASHBOARD_SCREENS_SECTION_md"
    /// </summary>
    private static string SanitizeNodeName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "unnamed";

        // Replace dots, hyphens, spaces, and other invalid chars with underscores
        string sanitized = Regex.Replace(fileName, @"[^a-zA-Z0-9_]", "_");

        // Ensure it doesn't start with a digit
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        // Collapse multiple underscores
        sanitized = Regex.Replace(sanitized, @"_{2,}", "_").Trim('_');

        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }
}
