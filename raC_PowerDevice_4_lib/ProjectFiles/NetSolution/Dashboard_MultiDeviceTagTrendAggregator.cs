#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
using FTOptix.UI;
using UAManagedCore;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;
using OpcUa = UAManagedCore.OpcUa;
#endregion


public class Dashboard_MultiDeviceTagTrendAggregator : BaseNetLogic
{
    // ==== Database Configuration ====
    private const string TABLE = "DataLogger_AllDevices";
    private const string TABLE_ENERGY = "EnergyLog_AllDevices";
    private const string STORE_PARENT_PATH =
        "Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores";
    private const string STORE_ALIAS_NAME =
        "raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality";
    private const string RES_OUTPUT_FOLDER = "res/TabCommon_TrendViewerDashboard";
    private const int MAX_TAG_SELECTION = 5;

    // Fixed Energy Log columns
    private static readonly List<string> ENERGY_LOG_COLUMNS = new()
    {
        "kW_Demand", "kVAR_Demand", "kVA_Demand", "Demand_PF",
        "Projected_kW_Demand", "Projected_kVAR_Demand", "Projected_kVA_Demand",
        "Total_kWh_Fwd", "Total_kWh_Rev", "Total_kWh_Net",
        "Total_kVARh_Fwd", "Total_kVARh_Rev", "Total_kVARh_Net", "Total_kVAh"
    };

    private static readonly HashSet<string> EXCLUDE_COLUMNS = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timestamp", "DeviceName", "SourceFile", "SourceFileName",
        "Record_ID", "RecordID", "Month_Day", "Hour_Minute", "Sec_MilliSec", "Year",
        "CreatedAt", "ID"
    };

    private Store dataStore;
    private string jsonDataPath = "";
    private IUAVariable selectedOrgNodeVar;
    private IUAVariable selectedDevicesVar;      // JSON array of selected device names
    private IUAVariable selectedTagsVar;         // JSON array of selected tag names
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private WebBrowser trendViewerBrowser;

    private Dictionary<string, CheckBox> tagCheckboxes = new();
    private Dictionary<string, raC_4_01_raC_Dvc_PM5000_PQEMDashboard_DeviceSelectButton> deviceButtons = new();
    private List<string> allDevices = new();                    // All devices in org node
    private IUANode devicePanel;
    private IUAVariable selectedDaysVar;
    private IUAVariable errorMessageVar;

    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-1);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 1;

    public override void Start()
    {
        try
        {
            // Get data store
            var storeAliasParent = Project.Current.GetObject(STORE_PARENT_PATH);
            dataStore = storeAliasParent?.Get<Store>(STORE_ALIAS_NAME);

            if (dataStore == null)
            {
                Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Store not found at {STORE_PARENT_PATH}/{STORE_ALIAS_NAME}");
                return;
            }

            // Initialize selection variables
            InitializeSelectionVariables();
            InitializeJsonPath();

            // Write initial "no selection" JSON so the HTML page has a valid file on first load
            if (!string.IsNullOrEmpty(jsonDataPath) && !File.Exists(jsonDataPath))
            {
                WriteInfoJson("No Devices or Tags Selected", "Select one or more devices and tags, then click Load to display trend data.");
            }

            // Get selected org node variable
            var navigationPanel = Owner.Owner.Owner.Owner;
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            if (selectedOrgNodeVar != null)
            {
                selectedOrgNodeVar.VariableChange += OnSelectedOrgNodeChanged;
            }

            // Initialize device panel
            try
            {
                devicePanel = Owner.Get<IUANode>("hl_devicelist");
                if (devicePanel != null)
                {
                    string currentPath = selectedOrgNodeVar?.Value?.Value?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(currentPath))
                    {
                        RefreshDeviceButtons(currentPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", $"Error finding device panel: {ex.Message}");
            }

            // Initialize date range
            startDateVar = Owner.GetVariable("StartDate");
            endDateVar = Owner.GetVariable("EndDate");
            selectedDaysVar = Owner.GetVariable("SelectedDays");
            errorMessageVar = Owner.GetVariable("ErrorMessage");
            
            if (startDateVar != null) startDateVar.Value = timeRangeStartDate;
            if (endDateVar != null) endDateVar.Value = timeRangeEndDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = timeRangeDays;
            if (errorMessageVar != null) errorMessageVar.Value = ""; // Clear error on start

            // Initialize UI accordion for tags
            try
            {
                var vl_Tags = Owner.Get<ColumnLayout>("HorizontalLayout1/Rectangle1/HorizontalLayout1/VerticalLayout1/TagScrollView/vl_Tags");
                if (vl_Tags != null)
                {
                    
                   InitializeTagCheckboxes(vl_Tags);
                   
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", $"Error initializing tag accordion: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Start failed: {ex.Message}");
        }
    }

    public override void Stop()
    {
        try
        {
            if (selectedOrgNodeVar != null)
            {
                selectedOrgNodeVar.VariableChange -= OnSelectedOrgNodeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", $"Error during Stop: {ex.Message}");
        }
    }

    // ==================================================
    // Event Handlers
    // ==================================================

    private void OnSelectedOrgNodeChanged(object sender, VariableChangeEventArgs e)
    {
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value?.Value?.ToString() ?? "";

            // Refresh device list and buttons
            RefreshDeviceButtons(selectedPath);

            // Clear device selections
            ClearDeviceSelections();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error in OnSelectedOrgNodeChanged: {ex.Message}");
        }
    }

    // ==================================================
    // Tag Accordion Initialization & Management
    // ==================================================

    private void InitializeTagCheckboxes(ColumnLayout vl_Tags)
    {
        try
        {
            // Query available tags from DataLogger
            dataStore.Query($"SELECT * FROM {Q(TABLE)} LIMIT 1", out string[] colsDataLog, out _);

            var tagCols = new List<string>();

            // Add Energy Log columns FIRST (these are important)
            tagCols.AddRange(ENERGY_LOG_COLUMNS);

            // Create a HashSet of energy columns for quick lookup (case-insensitive)
            var energyColumnsSet = new HashSet<string>(ENERGY_LOG_COLUMNS, StringComparer.OrdinalIgnoreCase);

            // Add DataLogger columns (excluding duplicates and sorted alphabetically)
            if (colsDataLog != null && colsDataLog.Length > 0)
            {
                var dataLogCols = colsDataLog
                    .Where(c => !string.IsNullOrWhiteSpace(c) 
                             && !EXCLUDE_COLUMNS.Contains(c)
                             && !energyColumnsSet.Contains(c))  // Exclude energy columns already added
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c)
                    .ToList();
                
                tagCols.AddRange(dataLogCols);
            }

            if (tagCols.Count == 0)
            {
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", "No tags available");
                return;
            }

           tagCheckboxes.Clear();

            // Remove only dynamically created checkboxes, keep Label1
            var existingCheckboxes = vl_Tags.Children.OfType<CheckBox>().ToList();
            foreach (var cb in existingCheckboxes)
            {
                vl_Tags.Remove(cb);
            }

            foreach (var tag in tagCols)
            {
                var checkbox = InformationModel.Make<CheckBox>($"Chk_{SanitizeNodeName(tag)}");
                checkbox.Text = FormatDisplayName(tag);
                checkbox.Checked = false;
                checkbox.Height = 30;
                checkbox.Width = -1;

                checkbox.OnUserValueChanged += (sender, e) =>
                {
                    OnTagCheckboxChanged(tag, checkbox.Checked);
                };

                vl_Tags.Add(checkbox);
                tagCheckboxes[tag] = checkbox;
            }            

            UpdateTagCheckboxStates();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error initializing tag accordion: {ex.Message}");
        }
    }

    private void OnTagCheckboxChanged(string tagName, bool isChecked)
    {
        try
        {
            // Check if trying to select more than allowed
            if (isChecked)
            {
                var selectedTags = GetSelectedTags();
                if (selectedTags.Count > MAX_TAG_SELECTION)
                {
                    // Revert the checkbox - user tried to select too many
                    if (tagCheckboxes.TryGetValue(tagName, out var checkbox))
                    {
                        checkbox.Checked = false;
                    }
                    return;
                }
            }

            UpdateTagCheckboxStates();
            UpdateSelectedTagsList();
            ExportMultiDeviceTagData();  // Auto-refresh
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error on tag change: {ex.Message}");
        }
    }

    private void UpdateTagCheckboxStates()
    {
        try
        {
            var selectedTags = GetSelectedTags();
            bool maxReached = selectedTags.Count >= MAX_TAG_SELECTION;

            // Enable/disable checkboxes based on selection count
            foreach (var kvp in tagCheckboxes)
            {
                kvp.Value.Enabled = kvp.Value.Checked || !maxReached;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", $"Error updating checkbox states: {ex.Message}");
        }
    }

    private List<string> GetSelectedTags()
    {
        var selected = new List<string>();
        foreach (var kvp in tagCheckboxes)
        {
            if (kvp.Value.Checked)
                selected.Add(kvp.Key);
        }
        return selected;
    }

    private void UpdateSelectedTagsList()
    {
        try
        {
            var selectedTags = GetSelectedTags();
            string jsonArray = JsonSerializer.Serialize(selectedTags);

            if (selectedTagsVar != null)
                selectedTagsVar.Value = jsonArray;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error updating tags list: {ex.Message}");
        }
    }

    // ==================================================
    // Device Button Management
    // ==================================================

    /// <summary>
    /// Refresh device button list based on selected org node
    /// </summary>
    private void RefreshDeviceButtons(string orgNodePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(orgNodePath))
            {
                allDevices.Clear();
                deviceButtons.Clear();
                return;
            }

            // Get all devices from org node
            allDevices = GetDeviceNamesFromOrgNode(orgNodePath);

            if (allDevices.Count == 0)
                return;

            // Create button instances in UI
            try
            {
                if (devicePanel != null)
                {
                    // Clear existing buttons
                    var existingButtons = new List<IUANode>(devicePanel.Children);
                    foreach (var btn in existingButtons)
                    {
                        devicePanel.Remove(btn);
                    }

                    deviceButtons.Clear();

                    // Create new buttons for each device
                    for (int i = 0; i < allDevices.Count; i++)
                    {
                        string deviceName = allDevices[i];
                        var btnInstance = InformationModel.MakeObject<raC_4_01_raC_Dvc_PM5000_PQEMDashboard_DeviceSelectButton>(
                            $"DeviceBtn_{SanitizeNodeName(deviceName)}");

                        var deviceNameVar = btnInstance.GetVariable("DeviceName");
                        if (deviceNameVar != null)
                            deviceNameVar.Value = deviceName;

                        var isSelectedVar = btnInstance.GetVariable("IsSelected");
                        if (isSelectedVar != null)
                            isSelectedVar.Value = false;

                        // Store reference
                        deviceButtons[deviceName] = btnInstance;


                        // Bind click handler - capture device name in closure
                        string capturedDeviceName = deviceName;
                        var btn = btnInstance.Get<Button>("Button1");
                        if (btn != null)
                        {
                            btn.UAEvent += (sender, e) =>
                            {
                                if (e.EventType.BrowseName == "MouseDownEvent")
                                {
                                    OnDeviceButtonClicked(capturedDeviceName);
                                }
                            };
                        }

                        devicePanel.Add(btnInstance);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", $"Error creating device buttons: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error refreshing device buttons: {ex.Message}");
        }
    }

    private void OnDeviceButtonClicked(string deviceName)
    {
        try
        {
            if (deviceButtons.TryGetValue(deviceName, out var buttonObj))
            {
                var isSelectedVar = buttonObj.GetVariable("IsSelected");
                if (isSelectedVar != null)
                {
                    bool current = false;
                    if (isSelectedVar.Value?.Value is bool b)
                        current = b;
                    isSelectedVar.Value = !current;
                }
            }
            UpdateSelectedDevicesList();
            ExportMultiDeviceTagData();  // Auto-refresh
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error on device button click: {ex.Message}");
        }
    }

    private List<string> GetSelectedDevices()
    {
        var selected = new List<string>();
        foreach (var deviceName in allDevices)
        {
            if (deviceButtons.TryGetValue(deviceName, out var buttonObj))
            {
                var isSelectedVar = buttonObj.GetVariable("IsSelected");
                if (isSelectedVar != null && isSelectedVar.Value?.Value is bool isSelected && isSelected)
                    selected.Add(deviceName);
            }
        }
        return selected;
    }

    private void UpdateSelectedDevicesList()
    {
        try
        {
            var selectedDevices = GetSelectedDevices();
            string jsonArray = JsonSerializer.Serialize(selectedDevices);

            if (selectedDevicesVar != null)
                selectedDevicesVar.Value = jsonArray;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error updating devices list: {ex.Message}");
        }
    }

    private void ClearDeviceSelections()
    {
        foreach (var btn in deviceButtons.Values)
        {
            var isSelectedVar = btn.GetVariable("IsSelected");
            if (isSelectedVar != null)
                isSelectedVar.Value = false;
        }
        if (selectedDevicesVar != null)
            selectedDevicesVar.Value = "[]";
    }

    // ==================================================
    // Time Period Selection Methods
    // ==================================================

    [ExportMethod]
    public void LoadTrend24Hours() { LoadTrendForDays(1); }

    [ExportMethod]
    public void LoadTrend7Days() { LoadTrendForDays(7); }

    [ExportMethod]
    public void LoadTrend30Days() { LoadTrendForDays(30); }

    [ExportMethod]
    public void LoadTrendForDays(int days)
    {
        try
        {
            DateTime endDate = DateTime.Now;
            DateTime startDate = endDate.AddDays(-days);

            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;
            timeRangeDays = days;

            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = days;
            if (errorMessageVar != null) errorMessageVar.Value = "";

            ExportMultiDeviceTagData();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error loading trend data for {days} days: {ex.Message}");
        }
    }

    [ExportMethod]
    public void LoadTrendCustomRange(DateTime startDate, DateTime endDate)
    {
        try
        {
            if (startDate >= endDate)
            {
                string errorMsg = "Error: Start date must be before end date";
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", errorMsg);
                if (errorMessageVar != null) errorMessageVar.Value = errorMsg;
                WriteInfoJson("Invalid Date Range", "Start date must be before end date.");
                RefreshBrowser();
                return;
            }

            // Clear error message on valid input
            if (errorMessageVar != null) errorMessageVar.Value = "";

            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;

            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = -1; // Custom range indicator

            int days = (int)Math.Round((endDate - startDate).TotalDays);
            timeRangeDays = days;

            ExportMultiDeviceTagData();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error loading custom range: {ex.Message}");
            if (errorMessageVar != null) errorMessageVar.Value = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Load custom range using values from StartDate and EndDate variables.
    /// This method is intended to be bound to a "Load" button in the UI.
    /// </summary>
    [ExportMethod]
    public void LoadTrendFromDateVariables()
    {
        try
        {
            if (!TryGetDateFromVariable(startDateVar, out DateTime start))
            {
                string errorMsg = "Error: Please enter a valid start date";
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", errorMsg);
                if (errorMessageVar != null) errorMessageVar.Value = errorMsg;
                WriteInfoJson("Invalid Start Date", "Please enter a valid start date.");
                RefreshBrowser();
                return;
            }

            if (!TryGetDateFromVariable(endDateVar, out DateTime end))
            {
                string errorMsg = "Error: Please enter a valid end date";
                Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", errorMsg);
                if (errorMessageVar != null) errorMessageVar.Value = errorMsg;
                WriteInfoJson("Invalid End Date", "Please enter a valid end date.");
                RefreshBrowser();
                return;
            }

            LoadTrendCustomRange(start, end);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error loading from date variables: {ex.Message}");
            if (errorMessageVar != null) errorMessageVar.Value = $"Error: {ex.Message}";
        }
    }

    // ==================================================
    // Data Export & JSON Generation
    // ==================================================

    [ExportMethod]
    public void ExportMultiDeviceTagData()
    {
        try
        {
            var selectedDevices = GetSelectedDevices();
            var selectedTags = GetSelectedTags();

            if (selectedDevices.Count == 0)
            {
                WriteInfoJson("No Devices Selected", "Please select at least one device from the device panel.");
                RefreshBrowser();
                return;
            }

            if (selectedTags.Count == 0)
            {
                WriteInfoJson("No Tags Selected", "Please select at least one tag from the tag accordion.");
                RefreshBrowser();
                return;
            }

            // Get date range
            if (!TryGetDateFromVariable(startDateVar, out DateTime start))
                start = timeRangeStartDate;
            if (!TryGetDateFromVariable(endDateVar, out DateTime end))
                end = timeRangeEndDate;

            ExportMultiDeviceTagJson(start, end, selectedDevices, selectedTags);
            RefreshBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Export failed: {ex.Message}");
        }
    }

    private void ExportMultiDeviceTagJson(DateTime start, DateTime end, List<string> devices, List<string> tags)
    {
        try
        {
            string startIso = ToIso(start);
            string endIso = ToIso(end);

            // Group tags by source table
            var dataLogTags = tags.Where(t => !IsEnergyLogTag(t)).ToList();
            var energyLogTags = tags.Where(t => IsEnergyLogTag(t)).ToList();

            // Build list of all expected series (device + tag combinations)
            var allSeriesKeys = new HashSet<string>();
            var seriesWithData = new HashSet<string>();
            foreach (var device in devices)
            {
                foreach (var tag in tags)
                {
                    allSeriesKeys.Add($"{device}_{tag}");
                }
            }

            // Series format: each entry = { ts, device, tag, v }
            var seriesData = new List<Dictionary<string, object>>();

            // Query DataLogger tags
            if (dataLogTags.Count > 0)
            {
                foreach (var device in devices)
                {
                    var tagList = string.Join(", ", dataLogTags.Select(t => Q(t)));
                    string sql = $"SELECT {Q("Timestamp")}, {tagList} " +
                                $"FROM {Q(TABLE)} " +
                                $"WHERE {Q("DeviceName")} = '{device.Replace("'", "''")}' " +
                                $"AND {Q("Timestamp")} >= \"{startIso}\" AND {Q("Timestamp")} <= \"{endIso}\" " +
                                $"ORDER BY {Q("Timestamp")} ASC";

                    dataStore.Query(sql, out _, out object[,] rows);

                    if (rows != null && rows.GetLength(0) > 0)
                    {
                        int rowCount = rows.GetLength(0);
                        for (int i = 0; i < rowCount; i++)
                        {
                            DateTime ts = ParseTimestamp(rows[i, 0]);
                            for (int j = 0; j < dataLogTags.Count; j++)
                            {
                                double? val = TryParseDouble(rows[i, j + 1]);
                                string seriesKey = $"{device}_{dataLogTags[j]}";
                                seriesWithData.Add(seriesKey);
                                seriesData.Add(new Dictionary<string, object>
                                {
                                    ["ts"] = ts == DateTime.MinValue ? "" : ToIso(ts),
                                    ["device"] = device,
                                    ["tag"] = dataLogTags[j],
                                    ["v"] = val,
                                    ["seriesKey"] = seriesKey
                                });
                            }
                        }
                    }
                }
            }

            // Query EnergyLog tags
            if (energyLogTags.Count > 0)
            {
                foreach (var device in devices)
                {
                    var tagList = string.Join(", ", energyLogTags.Select(t => Q(t)));
                    string sql = $"SELECT {Q("Timestamp")}, {tagList} " +
                                $"FROM {Q(TABLE_ENERGY)} " +
                                $"WHERE {Q("DeviceName")} = '{device.Replace("'", "''")}' " +
                                $"AND {Q("Timestamp")} >= \"{startIso}\" AND {Q("Timestamp")} <= \"{endIso}\" " +
                                $"ORDER BY {Q("Timestamp")} ASC";

                    dataStore.Query(sql, out _, out object[,] rows);

                    if (rows != null && rows.GetLength(0) > 0)
                    {
                        int rowCount = rows.GetLength(0);
                        for (int i = 0; i < rowCount; i++)
                        {
                            DateTime ts = ParseTimestamp(rows[i, 0]);
                            for (int j = 0; j < energyLogTags.Count; j++)
                            {
                                double? val = TryParseDouble(rows[i, j + 1]);
                                string seriesKey = $"{device}_{energyLogTags[j]}";
                                seriesWithData.Add(seriesKey);
                                seriesData.Add(new Dictionary<string, object>
                                {
                                    ["ts"] = ts == DateTime.MinValue ? "" : ToIso(ts),
                                    ["device"] = device,
                                    ["tag"] = energyLogTags[j],
                                    ["v"] = val,
                                    ["seriesKey"] = seriesKey
                                });
                            }
                        }
                    }
                }
            }

            // Build seriesInfo - all selected device+tag combos with hasData flag
            var seriesInfo = new List<Dictionary<string, object>>();
            foreach (var device in devices)
            {
                foreach (var tag in tags)
                {
                    string seriesKey = $"{device}_{tag}";
                    bool hasData = seriesWithData.Contains(seriesKey);
                    seriesInfo.Add(new Dictionary<string, object>
                    {
                        ["seriesKey"] = seriesKey,
                        ["device"] = device,
                        ["tag"] = tag,
                        ["displayName"] = $"{device} - {FormatDisplayName(tag)}",
                        ["hasData"] = hasData
                    });
                }
            }

            // Limit points
            const int MAX_POINTS = 50000;
            if (seriesData.Count > MAX_POINTS)
            {
                int step = (int)Math.Ceiling(seriesData.Count / (double)MAX_POINTS);
                seriesData = seriesData.Where((p, idx) => idx % step == 0).ToList();
            }

            // Create payload - always include seriesInfo even if no data
            var payload = new
            {
                devices = devices,
                tags = tags,
                start = ToIso(start),
                end = ToIso(end),
                count = seriesData.Count,
                seriesInfo = seriesInfo,  // Always includes all selected combinations
                series = seriesData
            };

            WriteJsonData(payload);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error exporting JSON: {ex.Message}");
        }
    }

    // ==================================================
    // JSON & File Operations
    // ==================================================

    private void InitializeJsonPath()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string logsDir = Path.Combine(baseDirectory, RES_OUTPUT_FOLDER);

            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);

            jsonDataPath = Path.Combine(logsDir, "MultiDeviceTagTrend_Data.json");
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error initializing JSON path: {ex.Message}");
        }
    }

    private void WriteJsonData(object data)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(jsonDataPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error writing JSON: {ex.Message}");
        }
    }

    private void WriteInfoJson(string title, string message)
    {
        try
        {
            var infoData = new
            {
                type = "info",
                title = title,
                message = message,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(infoData, options);
            File.WriteAllText(jsonDataPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_MultiDeviceTagTrendAggregator", $"Error writing info JSON: {ex.Message}");
        }
    }

    // ==================================================
    // Helper Methods
    // ==================================================

    private void InitializeSelectionVariables()
    {
        selectedDevicesVar = Owner.GetVariable("SelectedDevices");
        if (selectedDevicesVar == null)
        {
            selectedDevicesVar = InformationModel.MakeVariable("SelectedDevices", OpcUa.DataTypes.String);
            Owner.Add(selectedDevicesVar);
            selectedDevicesVar.Value = "[]";
        }

        selectedTagsVar = Owner.GetVariable("SelectedTags");
        if (selectedTagsVar == null)
        {
            selectedTagsVar = InformationModel.MakeVariable("SelectedTags", OpcUa.DataTypes.String);
            Owner.Add(selectedTagsVar);
            selectedTagsVar.Value = "[]";
        }
    }

    private List<string> GetDeviceNamesFromOrgNode(string orgNodePath)
    {
        try
        {
            var devices = new List<string>();

            if (string.IsNullOrWhiteSpace(orgNodePath))
                return devices;

            // Strip "Model/" prefix if present
            string cleanPath = orgNodePath;
            if (cleanPath.StartsWith("Model/"))
                cleanPath = cleanPath.Substring(6);

            var orgNode = Project.Current.GetObject("Model/" + cleanPath);
            if (orgNode == null)
                return devices;

            // Recursively collect device nodes
            CollectDeviceNodes(orgNode, devices);

            return devices
                .Distinct()
                .OrderBy(d => d)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private void CollectDeviceNodes(IUANode node, List<string> devices)
    {
        // Check if this is a device node (has DeviceAlarms and DeviceStatus)
        if (node.GetObject("DeviceAlarms") != null && node.GetObject("DeviceStatus") != null)
        {
            devices.Add(node.BrowseName);
        }

        // Recurse
        foreach (var child in node.Children)
        {
            CollectDeviceNodes(child, devices);
        }
    }

    private void RefreshBrowser()
    {
        try
        {
            trendViewerBrowser = Owner.Get<WebBrowser>("HorizontalLayout1/TrendviewerWebBrowser");
            if (trendViewerBrowser != null)
                trendViewerBrowser.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_MultiDeviceTagTrendAggregator", $"Error refreshing browser: {ex.Message}");
        }
    }

    private bool TryGetDateFromVariable(IUAVariable uaVar, out DateTime dt)
    {
        dt = DateTime.MinValue;
        if (uaVar == null) return false;

        var uaValue = uaVar.Value;
        if (uaValue == null) return false;

        object raw = uaValue.Value;
        if (raw == null) return false;

        if (raw is DateTime d)
        {
            dt = d;
            return true;
        }

        string s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim().Replace('T', ' ');

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return true;

        return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    private static string ToIso(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private DateTime ParseTimestamp(object tsObj)
    {
        if (tsObj == null || tsObj == DBNull.Value) return DateTime.MinValue;
        if (tsObj is DateTime dt) return dt;

        string s = tsObj.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;

        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            return parsed;

        return DateTime.MinValue;
    }

    private double? TryParseDouble(object vObj)
    {
        if (vObj == null || vObj == DBNull.Value) return null;

        if (vObj is IConvertible && vObj is not string)
        {
            try { return Convert.ToDouble(vObj, CultureInfo.InvariantCulture); }
            catch { }
        }

        var s = vObj.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
            return dv;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out dv))
            return dv;

        return null;
    }

    private string Q(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "\"\"";
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private string SanitizeNodeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var safe = new string(raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (char.IsDigit(safe[0])) safe = "_" + safe;
        return safe;
    }

    private bool IsEnergyLogTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return false;
        return ENERGY_LOG_COLUMNS.Any(c => string.Equals(c, tagName, StringComparison.OrdinalIgnoreCase));
    }

    private string FormatDisplayName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return "Unknown";

        try
        {
            string formatted = columnName.TrimEnd('_', '%');
            formatted = formatted.Replace('_', ' ');
            return formatted;
        }
        catch
        {
            return columnName;
        }
    }

}
