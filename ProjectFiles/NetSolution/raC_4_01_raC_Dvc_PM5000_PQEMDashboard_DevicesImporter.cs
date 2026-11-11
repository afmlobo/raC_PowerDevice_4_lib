#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.SerialPort;
using FTOptix.Core;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class raC_4_01_raC_Dvc_PM5000_PQEMDashboard_DevicesImporter : BaseNetLogic
{
    [ExportMethod]
    public void Import()
    {
        var csvVar = LogicObject.GetVariable("CSVPath");
        if (csvVar == null || csvVar.Value == null)
            throw new Exception("CSVPath (ResourceUri) is not set.");
        var csvPath = new ResourceUri(csvVar.Value).Uri;

        if (!File.Exists(csvPath))
            throw new Exception($"CSV file not found: {csvPath}");

        // Get the DeviceType from NetLogic variable
        var deviceTypeVar = LogicObject.GetVariable("DeviceType");
        if (deviceTypeVar == null || string.IsNullOrWhiteSpace(deviceTypeVar.Value))
            throw new Exception("DeviceType variable is not set on the NetLogic object.");
        string deviceType = deviceTypeVar.Value;

        // Verify the device type exists in the project
        var widgetType = Project.Current?.Find(deviceType);
        if (widgetType == null)
            throw new Exception($"Device type '{deviceType}' not found in project.");

        var lines = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count < 2) throw new Exception("CSV file is empty or only has header.");

        var header = SplitCsv(lines[0]);
        int deviceNameIdx = Array.IndexOf(header, "DeviceName");
        if (deviceNameIdx < 0) throw new Exception("CSV must include a DeviceName column.");

        // Hierarchy columns (before DeviceName)
        var hierarchyCols = header.Take(deviceNameIdx).ToArray();
        // Configuration columns (after DeviceName)
        var configCols = header.Skip(deviceNameIdx + 1).ToArray();

        // Get or create Model/DeviceType_Model folder
        var modelNode = Project.Current.Get("Model");
        if (modelNode == null)
            throw new Exception("Model folder not found in project.");

        // Verify raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType exists
        var raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType = Project.Current?.Find("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType");
        if (raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType == null)
            throw new Exception("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType not found in project. Please create it first.");

        var deviceModelFolder = EnsureHierarchyNode(modelNode, "raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model", "raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model", raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType.NodeId, "Model", "raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model");
        
        // Create hardcoded organization root
        var orgRoot = EnsureOrganizationNode(deviceModelFolder, "PM5000_PQEM_OrgModel", raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType.NodeId);
        
        int newDevices = 0, updatedDevices = 0, skippedRows = 0;

        foreach (var line in lines.Skip(1))
        {
            var cols = SplitCsv(line);
            if (cols.Length <= deviceNameIdx)
            {
                skippedRows++;
                continue;
            }

            // Check DeviceName first - skip row if empty (don't create empty org nodes)
            string deviceName = cols[deviceNameIdx].Trim();
            if (string.IsNullOrEmpty(deviceName))
            {
                skippedRows++;
                continue;
            }

            IUANode parent = orgRoot;
            
            // Build hierarchy (Enterprise -> Company -> Area -> Plant -> Cell -> Group)
            // Track the path for NodePath property
            var pathParts = new List<string>();
            
            for (int i = 0; i < hierarchyCols.Length; i++)
            {
                var hName = hierarchyCols[i];
                var hVal = Get(cols, header, hName)?.Trim();
                if (string.IsNullOrEmpty(hVal)) continue;
                
                pathParts.Add(hVal);
                var browseName = San(hVal);
                var nodePath = string.Join(" > ", pathParts);
                
                parent = EnsureHierarchyNode(parent, browseName, $"{hName}: {hVal}", raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType.NodeId, hName, nodePath);
            }

            var deviceBrowseName = San(deviceName);
            
            // Check for duplicate device name across the entire tree
            var existingDeviceInTree = FindDeviceInTree(orgRoot, deviceBrowseName);
            
            if (existingDeviceInTree != null)
            {
                // Check if it's in the same parent (update allowed)
                var existingDeviceInCurrentParent = parent.Get(deviceBrowseName);
                
                if (existingDeviceInCurrentParent != null)
                {
                    // Update existing device in same parent
                    UpdateConfigVariables(existingDeviceInCurrentParent, cols, header, configCols);
                    updatedDevices++;
                    Log.Info("DevicesImporter", $"Updated device: {deviceName}");
                }
                else
                {
                    // Device exists elsewhere in the tree - skip
                    skippedRows++;
                    Log.Warning("DevicesImporter", $"Skipped duplicate device '{deviceName}' - already exists at: {GetNodePath(existingDeviceInTree)}");
                }
                continue;
            }

            // Create new device using the configured device type
            var devNode = InformationModel.MakeObject(deviceBrowseName, widgetType.NodeId);
            devNode.DisplayName = new LocalizedText(deviceName);
            parent.Add(devNode);

            UpdateConfigVariables(devNode, cols, header, configCols);
            newDevices++;
            Log.Info("DevicesImporter", $"Created device: {deviceName}");
        }

        // After all devices are created, update ChildDeviceCount for all parent nodes
        UpdateChildDeviceCounts(orgRoot);

        Log.Info("DevicesImporter", $"Import complete: {newDevices} new, {updatedDevices} updated, {skippedRows} skipped.");
    }

    // Recursively count devices under each parent node
    private void UpdateChildDeviceCounts(IUANode node)
    {
        int deviceCount = 0;
        
        foreach (var child in node.Children.OfType<IUANode>())
        {
            if (child == null) continue;
            
            // Skip variables - only process objects
            if (child is IUAVariable) continue;
            
            // Check if child is raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType by checking for NodeLevel variable
            var nodeLevelVar = child.GetVariable("NodeLevel");
            bool isOrgNode = (nodeLevelVar != null);
            
            if (isOrgNode)
            {
                // Recursively count devices in child org nodes
                UpdateChildDeviceCounts(child);
                var childCountVar = child.GetVariable("ChildDeviceCount");
                if (childCountVar != null && childCountVar.Value != null)
                {
                    int childDevices = Convert.ToInt32(childCountVar.Value.Value);
                    deviceCount += childDevices;
                    Log.Debug("DevicesImporter", $"  {node.BrowseName} -> {child.BrowseName} contributes {childDevices} devices");
                }
            }
            else
            {
                // It's a device node (doesn't have NodeLevel variable)
                deviceCount++;
                Log.Debug("DevicesImporter", $"  {node.BrowseName} -> Found device: {child.BrowseName}");
            }
        }
        
        // Set ChildDeviceCount on current node
        var countVar = node.GetVariable("ChildDeviceCount");
        if (countVar != null)
        {
            countVar.Value = deviceCount;
            Log.Info("DevicesImporter", $"{node.BrowseName} TOTAL: {deviceCount} devices");
        }
    }

    private void UpdateConfigVariables(IUANode deviceNode, string[] cols, string[] header, string[] configCols)
    {
        foreach (var cfgCol in configCols)
        {
            var raw = Get(cols, header, cfgCol);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var varNode = deviceNode.GetVariable(cfgCol);
            if (varNode != null)
            {
                // Update existing variable only
                try
                {
                    var newValue = ConvertValue(raw, varNode.DataType, cfgCol);
                    if (!Equals(varNode.Value.Value, newValue))
                    {
                        varNode.Value = new UAValue(newValue);
                        Log.Debug("DevicesImporter", $"Updated {deviceNode.BrowseName}.{cfgCol} = {newValue}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("DevicesImporter", $"Failed to update {cfgCol}: {ex.Message}");
                }
            }
            else
            {
                // Variable doesn't exist - log warning but don't create
                Log.Warning("DevicesImporter", $"Variable '{cfgCol}' not found on device type. Skipping.");
            }
        }
    }

    private object ConvertValue(string raw, NodeId dataType, string varName)
    {
        // Only treat as Duration if explicitly Cfg_PQEventsUpdatePeriod OR actual DataType is Duration
        if (dataType == OpcUa.DataTypes.Duration || 
            varName.Equals("Cfg_PQEventsUpdatePeriod", StringComparison.OrdinalIgnoreCase))
        {
            var ms = ParseDurationToMilliseconds(raw);
            if (ms < 0)
            {
                Log.Warning("DevicesImporter", $"Invalid Duration for {varName}: '{raw}', defaulting to 0 ms");
                ms = 0;
            }
            return ms;
        }

        // Booleans
        if (dataType == OpcUa.DataTypes.Boolean)
            return bool.TryParse(raw, out var b) ? b : false;

        // Int32
        if (dataType == OpcUa.DataTypes.Int32)
            return int.TryParse(raw, out var i) ? i : 0;

        // Double
        if (dataType == OpcUa.DataTypes.Double)
            return double.TryParse(raw, out var d) ? d : 0.0;

        // Fallback to string
        return raw.Trim();
    }

    private static double ParseDurationToMilliseconds(string raw)
    {
        raw = raw.Trim();
        
        // TimeSpan.TryParse handles "hh:mm:ss", "hh:mm:ss.fff", "d.hh:mm:ss"
        if (TimeSpan.TryParse(raw, out var ts))
            return ts.TotalMilliseconds;

        // Handle unit-suffixed forms (e.g., "60s", "900ms", "1m", "1h")
        var lower = raw.ToLowerInvariant();
        if (lower.EndsWith("ms") && double.TryParse(lower[..^2], out var vms))
            return vms;
        if (lower.EndsWith("s") && double.TryParse(lower[..^1], out var vs))
            return vs * 1000.0;
        if (lower.EndsWith("m") && double.TryParse(lower[..^1], out var vm))
            return vm * 60_000.0;
        if (lower.EndsWith("h") && double.TryParse(lower[..^1], out var vh))
            return vh * 3600_000.0;

        // Plain number: assume milliseconds
        if (double.TryParse(lower, out var plainMs))
            return plainMs;

        // Manual parsing for "hh:mm:ss.fff" format
        var parts = raw.Split(':');
        if (parts.Length >= 2)
        {
            int hours = 0, minutes = 0;
            double seconds = 0;

            int.TryParse(parts[0], out hours);
            int.TryParse(parts[1], out minutes);

            if (parts.Length >= 3)
            {
                double.TryParse(parts[2], out seconds);
            }
            
            return (hours * 3600.0 + minutes * 60.0 + seconds) * 1000.0;
        }

        return 0d;
    }

    private NodeId InferDataType(string raw, string varName)
    {
        // Only Cfg_PQEventsUpdatePeriod is explicitly a Duration type
        if (varName.Equals("Cfg_PQEventsUpdatePeriod", StringComparison.OrdinalIgnoreCase))
            return OpcUa.DataTypes.Duration;

        // Check for boolean (TRUE/FALSE)
        if (bool.TryParse(raw, out _)) 
            return OpcUa.DataTypes.Boolean;

        // Check for integer (5000, 900, etc.)
        if (int.TryParse(raw, out _)) 
            return OpcUa.DataTypes.Int32;

        // Check for double
        if (double.TryParse(raw, out _)) 
            return OpcUa.DataTypes.Double;

        return OpcUa.DataTypes.String;
    }

    private static string[] SplitCsv(string line)
        => line.Split(',').Select(s => s.Trim().Trim('"')).ToArray();

    private static string Get(string[] cols, string[] header, string name)
    {
        int idx = Array.IndexOf(header, name);
        return idx >= 0 && idx < cols.Length ? cols[idx] : null;
    }

    // Ensure Organization root exists
    private IUANode EnsureOrganizationNode(IUANode parentNode, string orgName, NodeId orgNodeTypeId)
    {
        var existing = parentNode.Get(orgName);
        if (existing != null) return existing;

        var orgNode = InformationModel.MakeObject(orgName, orgNodeTypeId);
        orgNode.DisplayName = new LocalizedText(orgName);
        
        // Set properties
        SetOrgNodeProperties(orgNode, "Root", orgName, "Organization Root");
        
        parentNode.Add(orgNode);
        Log.Info("DevicesImporter", $"Created '{orgName}' organization root");
        return orgNode;
    }

    // Create hierarchy nodes (Enterprise, Company, Area, Plant, Cell, Group) as raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType
    private IUANode EnsureHierarchyNode(IUANode parent, string browseName, string displayName, NodeId orgNodeTypeId, string nodeLevel, string nodePath)
    {
        var existing = parent.Get(browseName);
        if (existing != null) return existing;

        // Create as raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType Object
        var node = InformationModel.MakeObject(browseName, orgNodeTypeId);
        node.DisplayName = new LocalizedText(displayName);
        
        // Set properties
        SetOrgNodeProperties(node, nodeLevel, nodePath, displayName);
        
        parent.Add(node);
        return node;
    }

    // Set raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType properties
    private void SetOrgNodeProperties(IUANode node, string nodeLevel, string nodePath, string description)
    {
        // Set NodeLevel
        var levelVar = node.GetVariable("NodeLevel");
        if (levelVar != null)
            levelVar.Value = nodeLevel;

        // Set NodePath
        var pathVar = node.GetVariable("NodePath");
        if (pathVar != null)
            pathVar.Value = nodePath;

        // Set Description
        var descVar = node.GetVariable("Description");
        if (descVar != null)
            descVar.Value = description;

        // Initialize ChildDeviceCount to 0 (will be updated later)
        var countVar = node.GetVariable("ChildDeviceCount");
        if (countVar != null)
            countVar.Value = 0;
    }

    // Sanitize strings for BrowseName (no spaces, slashes, or special characters)
    private static string San(string s)
        => string.IsNullOrWhiteSpace(s) ? "Unknown" :
           new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // Recursively search for a device with the given BrowseName in the entire tree
    private IUANode FindDeviceInTree(IUANode node, string deviceBrowseName)
    {
        if (node == null) return null;

        foreach (var child in node.Children.OfType<IUANode>())
        {
            if (child is IUAVariable) continue;

            // Check if this child is an organization node (has NodeLevel variable)
            var nodeLevelVar = child.GetVariable("NodeLevel");
            bool isOrgNode = (nodeLevelVar != null);

            if (isOrgNode)
            {
                // Recursively search in organization nodes
                var found = FindDeviceInTree(child, deviceBrowseName);
                if (found != null) return found;
            }
            else if (child.BrowseName == deviceBrowseName)
            {
                // Found a device with matching BrowseName
                return child;
            }
        }

        return null;
    }

    // Get the full path of a node for logging purposes
    private string GetNodePath(IUANode node)
    {
        if (node == null) return "";

        var pathParts = new List<string>();
        var current = node;

        while (current != null && current.BrowseName != "PM5000_PQEM_OrgModel")
        {
            pathParts.Insert(0, current.BrowseName);
            current = current.Owner as IUANode;
        }

        return string.Join(" > ", pathParts);
    }

  
}
