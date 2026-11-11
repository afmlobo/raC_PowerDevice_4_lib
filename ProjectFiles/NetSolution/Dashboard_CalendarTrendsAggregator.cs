#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpcUa = UAManagedCore.OpcUa;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.UI;
using FTOptix.SQLiteStore;
using FTOptix.Core;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;
#endregion


public class Dashboard_CalendarTrendsAggregator : BaseNetLogic
{
    // ==== Database Configuration ====
    private const string TABLE = "DataLogger_AllDevices";
    private const string TABLE_ENERGY = "EnergyLog_AllDevices";
    private const string STORE_PARENT_PATH =
        "Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores";
    private const string STORE_ALIAS_NAME =
        "raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality";

    // Columns to exclude from tag combobox
    private static readonly HashSet<string> EXCLUDE_COLUMNS = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timestamp", "DeviceName", "SourceFile", "SourceFileName",
        "Record_ID", "RecordID", "Month_Day", "Hour_Minute", "Sec_MilliSec", "Year",
        "CreatedAt", "ID"
    };

    // Fixed Energy Log columns from EnergyLog_AllDevices table
    // These columns are configured at system level and cannot be customized by users
    private static readonly List<string> ENERGY_LOG_COLUMNS = new()
    {
        "kW_Demand", "kVAR_Demand", "kVA_Demand", "Demand_PF",
        "Projected_kW_Demand", "Projected_kVAR_Demand", "Projected_kVA_Demand",
        "Total_kWh_Fwd", "Total_kWh_Rev", "Total_kWh_Net",
        "Total_kVARh_Fwd", "Total_kVARh_Rev", "Total_kVARh_Net", "Total_kVAh"
    };


    private Store dataStore;
    private string jsonDataPath = "";
    private ComboBox cb_Tag;
    private ComboBox cb_Month;
    private ComboBox cb_Year;
    private IUAVariable selectedTagVar;
    private IUAVariable selectedOrgNodeVar;


    public override void Start()
    {
        try
        {
            var navigationPanel = Owner.Owner.Owner;
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            
            if (selectedOrgNodeVar == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "SelectedOrgNodePath variable not found");
                selectedOrgNodeVar = InformationModel.MakeVariable("SelectedOrgNodePath", OpcUa.DataTypes.String);
                LogicObject.Add(selectedOrgNodeVar);
                selectedOrgNodeVar.Value = "";
            }
            else
            {
                selectedOrgNodeVar.VariableChange += OnSelectedOrgNodeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_CalendarTrendsAggregator", "Error getting SelectedOrgNodePath: " + ex.Message);
        }

        try
        {
            // Get data store
            var storeAliasParent = Project.Current.GetObject(STORE_PARENT_PATH);
            dataStore = storeAliasParent?.Get<Store>(STORE_ALIAS_NAME);

            if (dataStore == null)
            {
                Log.Error("Dashboard_CalendarTrendsAggregator", "Data store not found. Check STORE_PARENT_PATH / STORE_ALIAS_NAME.");
                return;
            }

            // Initialize JSON path
            InitializeJsonPath();

            cb_Tag = Owner.Get<ComboBox>("FilterPanel/VerticalLayout1/HorizontalLayout1/cb_Tag");
            cb_Month = Owner.Get<ComboBox>("FilterPanel/VerticalLayout1/HorizontalLayout1/cb_Month");
            cb_Year = Owner.Get<ComboBox>("FilterPanel/VerticalLayout1/HorizontalLayout1/cb_Year");



            // Find UI controls
            // tagComboBox = FindFirstByName<ComboBox>(Owner, "TagCombo") ?? 
            //               FindFirstByName<ComboBox>(Owner, "cb_Tag");
            selectedTagVar = FindVariableByName(Owner, "SelectedTag");

            // Ensure SelectedTag exists
            if (selectedTagVar == null)
            {
                selectedTagVar = InformationModel.MakeVariable("SelectedTag", OpcUa.DataTypes.String);
                Owner.Add(selectedTagVar);
                selectedTagVar.Value = "";
            }

            // Initialize comboboxes
            if (cb_Tag != null)
            {
                InitializeTagCombo();
                
            }

            if (cb_Month != null)
            {
                InitializeMonthCombo();
            }

            if (cb_Year != null)
            {
                InitializeYearCombo();
            }

            InitializeWithSelectedNode();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"Start failed: {ex.Message}");
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
            Log.Warning("Dashboard_CalendarTrendsAggregator", "Error unsubscribing: " + ex.Message);
        }
    }

    /// <summary>
    /// Event handler: Called when org node selection changes in navigation tree
    /// Automatically reloads calendar data for new org node and refreshes browser
    /// </summary>
    private void OnSelectedOrgNodeChanged(object sender, VariableChangeEventArgs e)
    {
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value?.Value?.ToString() ?? "";
            
            // Auto-reload calendar data with new node
            OnMonthYearChanged();
            
            // Refresh browser to update UI
            RefreshCalendarDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"Error in OnSelectedOrgNodeChanged: {ex.Message}");
        }
    }



  
    [ExportMethod]
    public object[,] GetMonthsForCombo()
    {
        try
        {
            string[] monthNames = {
                "January", "February", "March", "April", "May", "June",
                "July", "August", "September", "October", "November", "December"
            };

            object[,] result = new object[12, 2];

            for (int i = 0; i < 12; i++)
            {
                result[i, 0] = (i + 1); // Month number (1-12)
                result[i, 1] = monthNames[i]; // Month name
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetMonthsForCombo failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Populate Year combobox
    /// Returns: object[,] with years (current year and previous 2 years)
    /// </summary>
    [ExportMethod]
    public object[,] GetYearsForCombo()
    {
        try
        {
            int currentYear = DateTime.Now.Year;
            int yearsToShow = 3; // Current year + 2 previous years

            object[,] result = new object[yearsToShow, 1];

            for (int i = 0; i < yearsToShow; i++)
            {
                result[i, 0] = (currentYear - i);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetYearsForCombo failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Get current month and year for default selection
    /// Returns: object[,] with current month and year
    /// </summary>
    [ExportMethod]
    public object[,] GetCurrentMonthYear()
    {
        try
        {
            object[,] result = new object[1, 2];
            result[0, 0] = DateTime.Now.Month;
            result[0, 1] = DateTime.Now.Year;

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetCurrentMonthYear failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Get next month for navigation
    /// </summary>
    [ExportMethod]
    public object[,] GetNextMonth(int currentMonth, int currentYear)
    {
        try
        {
            int nextMonth = currentMonth + 1;
            int nextYear = currentYear;

            if (nextMonth > 12)
            {
                nextMonth = 1;
                nextYear++;
            }

            object[,] result = new object[1, 2];
            result[0, 0] = nextMonth;
            result[0, 1] = nextYear;

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetNextMonth failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Get previous month for navigation
    /// </summary>
    [ExportMethod]
    public object[,] GetPreviousMonth(int currentMonth, int currentYear)
    {
        try
        {
            int prevMonth = currentMonth - 1;
            int prevYear = currentYear;

            if (prevMonth < 1)
            {
                prevMonth = 12;
                prevYear--;
            }

            object[,] result = new object[1, 2];
            result[0, 0] = prevMonth;
            result[0, 1] = prevYear;

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetPreviousMonth failed: {ex.Message}");
            return new object[0, 0];
        }
    }



    #region Step 2: Query Database for Available Tags

    /// <summary>
    /// Initialize tag combobox by combining DataLogger_AllDevices columns and fixed EnergyLog columns
    /// DataLogger columns are discovered dynamically, EnergyLog columns are predefined
    /// All columns are merged, deduplicated, and sorted alphabetically
    /// </summary>
    private void InitializeTagCombo()
    {
        try
        {
            if (cb_Tag == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "TagComboBox not found on this screen.");
                return;
            }

            if (dataStore == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "DataStore not initialized");
                return;
            }

            // Collect columns from both DataLogger and Energy Log
            var allCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Query DataLogger_AllDevices table schema
            dataStore.Query($"SELECT * FROM {Q(TABLE)} LIMIT 1", out string[] colsDataLog, out _);
            if (colsDataLog != null && colsDataLog.Length > 0)
            {
                foreach (var col in colsDataLog)
                {
                    if (!string.IsNullOrWhiteSpace(col) && !EXCLUDE_COLUMNS.Contains(col))
                    {
                        allCols.Add(col);
                    }
                }
            }
            else
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "No columns found in DataLogger_AllDevices table.");
            }

            // Add fixed Energy Log columns
            int countBefore = allCols.Count;
            foreach (var col in ENERGY_LOG_COLUMNS)
            {
                if (!string.IsNullOrWhiteSpace(col) && !allCols.Contains(col))
                {
                    allCols.Add(col);
                }
            }

            if (allCols.Count == 0)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "No valid tag columns found after filtering from both DataLogger and Energy Log.");
                return;
            }

            // Sort all collected columns
            var tagCols = allCols.OrderBy(c => c).ToList();

            // Build a model for the ComboBox
            var modelRoot = Project.Current.GetObject("Model");
            if (modelRoot == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Model not found in project.");
                return;
            }

            // Delete existing model if it exists
            var existing = modelRoot?.GetObject("CalendarTrendTagListModel");
            existing?.Delete();

            // Create new tag list model
            var tagModel = InformationModel.MakeObject("CalendarTrendTagListModel");
            modelRoot?.Add(tagModel);

            // Add each column as a combobox item with display names
            foreach (var col in tagCols)
            {
                // Create item with sanitized node name
                var item = InformationModel.MakeVariable(SanitizeNodeName(col), OpcUa.DataTypes.String);
                // Store actual column name as Value (used by backend)
                item.Value = col;
                // Add DisplayName variable (shown to user in UI)
                var displayNameVar = InformationModel.MakeVariable("UIDisplayName", OpcUa.DataTypes.String);
                displayNameVar.Value = FormatDisplayName(col);
                item.Add(displayNameVar);
                tagModel.Add(item);
            }

            cb_Tag.Model = tagModel.NodeId;
           

            // IMPORTANT: Configure ComboBox in UI Designer to show DisplayName
            // Set ComboBox properties:
            // - Display value path: "{item}/UIDisplayName"
            // - Selected value path: "{item}"
            // This will show friendly names to user while using actual column names for backend

            // Set default selection to first tag
            var defaultTag = tagCols.Count > 0 ? tagCols[0] : "";
            selectedTagVar.Value = defaultTag;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"InitializeTagCombo failed: {ex.Message}");
        }
    }


    [ExportMethod]
    public object[,] GetAvailableTagsArray()
    {
        try
        {
            if (dataStore == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "DataStore not initialized");
                return new object[0, 0];
            }

            // Collect columns from both DataLogger and Energy Log
            var allCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Query DataLogger_AllDevices table schema
            dataStore.Query($"SELECT * FROM {Q(TABLE)} LIMIT 1", out string[] colsDataLog, out _);
            if (colsDataLog != null)
            {
                foreach (var col in colsDataLog)
                {
                    if (!string.IsNullOrWhiteSpace(col) && !EXCLUDE_COLUMNS.Contains(col))
                    {
                        allCols.Add(col);
                    }
                }
            }

            // Add fixed Energy Log columns
            foreach (var col in ENERGY_LOG_COLUMNS)
            {
                if (!string.IsNullOrWhiteSpace(col) && !allCols.Contains(col))
                {
                    allCols.Add(col);
                }
            }

            if (allCols.Count == 0)
                return new object[0, 0];

            // Sort columns
            var tagCols = allCols.OrderBy(c => c).ToList();

            // Return as 2D array
            object[,] result = new object[tagCols.Count, 1];
            for (int i = 0; i < tagCols.Count; i++)
            {
                result[i, 0] = tagCols[i];
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetAvailableTagsArray failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Initialize month combobox by creating a model with 12 months
    /// </summary>
    private void InitializeMonthCombo()
    {
        try
        {
            if (cb_Month == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Month ComboBox not found on this screen.");
                return;
            }

            var modelRoot = Project.Current.GetObject("Model");
            if (modelRoot == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Model not found in project.");
                return;
            }

            // Delete existing model if it exists
            var existing = modelRoot?.GetObject("CalendarTrendMonthListModel");
            existing?.Delete();

            // Create new month list model
            var monthModel = InformationModel.MakeObject("CalendarTrendMonthListModel");
            modelRoot?.Add(monthModel);

            string[] monthNames = {
                "January", "February", "March", "April", "May", "June",
                "July", "August", "September", "October", "November", "December"
            };

            // Add each month as a combobox item
            // Use numeric value as BrowseName so combobox stores and returns numeric strings
            for (int i = 0; i < 12; i++)
            {
                int monthNum = i + 1;
                var item = InformationModel.MakeVariable(monthNames[i], OpcUa.DataTypes.String);
                item.Value = monthNum.ToString(); // Store month number (1-12)
                
                // Add a description variable to display the month name
                //item.Description = new LocalizedText("en", monthNames[i]);
                // var descItem = InformationModel.MakeVariable($"Description", OpcUa.DataTypes.String);
                // descItem.Value = monthNames[i];
                // item.Add(descItem);
                
                monthModel.Add(item);
            }

            cb_Month.Model = monthModel.NodeId;

            // Set default selection to current month
            int currentMonth = DateTime.Now.Month;
            if (currentMonth >= 1 && currentMonth <= 12)
            {
                var selectedValueVar = cb_Month.GetVariable("SelectedValue");
                if (selectedValueVar != null)
                    selectedValueVar.Value = currentMonth.ToString();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"InitializeMonthCombo failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialize year combobox by creating a model with current year and 2 previous years
    /// </summary>
    private void InitializeYearCombo()
    {
        try
        {
            if (cb_Year == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Year ComboBox not found on this screen.");
                return;
            }

            var modelRoot = Project.Current.GetObject("Model");
            if (modelRoot == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Model not found in project.");
                return;
            }

            // Delete existing model if it exists
            var existing = modelRoot?.GetObject("CalendarTrendYearListModel");
            existing?.Delete();

            // Create new year list model
            var yearModel = InformationModel.MakeObject("CalendarTrendYearListModel");
            modelRoot?.Add(yearModel);

            int currentYear = DateTime.Now.Year;
            int yearsToShow = 10; // Current year + 2 previous years

            // Add years as combobox items
            for (int i = 0; i < yearsToShow; i++)
            {
                int year = currentYear - i;
                var item = InformationModel.MakeVariable(year.ToString(), OpcUa.DataTypes.String);
                item.Value = year.ToString(); // Display year as string
                yearModel.Add(item);
            }

            cb_Year.Model = yearModel.NodeId;
            

            // Set default selection to current year
            var selectedValueVar = cb_Year.GetVariable("SelectedValue");
            if (selectedValueVar != null)
                selectedValueVar.Value = currentYear;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"InitializeYearCombo failed: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods for Parsing

    /// <summary>
    /// Parse database timestamp from various formats
    /// </summary>
    private DateTime ParseDbTimestamp(object tsObj)
    {
        try
        {
            tsObj = UnwrapValue(tsObj);
            if (tsObj == null || tsObj == DBNull.Value)
                return DateTime.MinValue;

            if (tsObj is DateTime dt)
                return dt;

            var s = tsObj.ToString();
            if (string.IsNullOrWhiteSpace(s))
                return DateTime.MinValue;

            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
                return parsed;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AssumeLocal, out parsed))
                return parsed;

            return DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Parse double value from various formats
    /// </summary>
    private double? TryParseDouble(object vObj)
    {
        try
        {
            vObj = UnwrapValue(vObj);
            if (vObj == null || vObj == DBNull.Value)
                return null;

            if (vObj is IConvertible && vObj is not string)
            {
                try { return Convert.ToDouble(vObj, System.Globalization.CultureInfo.InvariantCulture); }
                catch { }
            }

            if (vObj is byte[] bytes)
            {
                try
                {
                    if (bytes.Length >= 8) return BitConverter.ToDouble(bytes, 0);
                    if (bytes.Length >= 4) return (double)BitConverter.ToSingle(bytes, 0);
                }
                catch { }
                return null;
            }

            var s = vObj.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv))
                return dv;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out dv))
                return dv;

            var match = System.Text.RegularExpressions.Regex.Match(s, @"[-+]?\d+(?:[.,]\d+)?");
            if (!match.Success) return null;

            var num = match.Value.Replace(',', '.');
            if (double.TryParse(num, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out dv))
                return dv;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Unwrap value from UAValue wrapper
    /// </summary>
    private object UnwrapValue(object obj)
    {
        if (obj == null || obj == DBNull.Value)
            return null;
        if (obj is UAValue ua)
            return ua.Value;

        var t = obj.GetType();
        var valueProp = t.GetProperty("Value");
        if (valueProp != null && valueProp.CanRead && valueProp.GetIndexParameters().Length == 0)
        {
            try
            {
                var inner = valueProp.GetValue(obj);
                if (!ReferenceEquals(inner, obj))
                    return inner;
            }
            catch { }
        }
        return obj;
    }

    #endregion

    #region Step 3: Pull Multi-Device Data for Calendar Trends

   
    [ExportMethod]
    public string PullCalendarTrendData(string nodeNameOrPath, string tagName, int month, int year)
    {
        try
        {
            if (dataStore == null)
            {
                Log.Error("Dashboard_CalendarTrendsAggregator", "DataStore not initialized");
                return "";
            }

            if (string.IsNullOrWhiteSpace(nodeNameOrPath) || string.IsNullOrWhiteSpace(tagName))
            {
                Log.Error("Dashboard_CalendarTrendsAggregator", "Invalid node name or tag name");
                return "";
            }

            // Step 1: Discover all PM5000 devices in org under the selected node
            var devices = GetDeviceNamesFromOrgNode(nodeNameOrPath);
            if (devices.Count == 0)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", $"No PM5000 devices found under node {nodeNameOrPath}");
            }

            // Step 2: Create calendar trend data container
            var calendarData = new CalendarTrendData
            {
                SelectedNode = StripModelPrefix(nodeNameOrPath),
                SelectedTag = tagName,
                SelectedMonth = month,
                SelectedYear = year,
                GeneratedAt = DateTime.Now
            };

            // Step 3: For each device, query data for the entire month
            foreach (var device in devices)
            {
                var deviceTrendData = PullDeviceDataForMonth(device, tagName, month, year);
                if (deviceTrendData != null)
                {
                    calendarData.Devices.Add(deviceTrendData);
                }
            }

            // Step 4: Save to JSON
            SaveCalendarTrendData(calendarData);

            // Step 5: Return file path
            return jsonDataPath;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"PullCalendarTrendData failed: {ex.Message}\n{ex.StackTrace}");
            return "";
        }
    }

    /// <summary>
    /// Recursively collect device nodes (matching Energy_LogAggregator pattern)
    /// Devices are identified by having DeviceAlarms and DeviceStatus objects
    /// </summary>
    private void CollectDeviceNodes(IUANode node, List<IUANode> devices)
    {
        // Check if this node is a device (has DeviceAlarms and DeviceStatus)
        if (node.GetObject("DeviceAlarms") != null && node.GetObject("DeviceStatus") != null)
        {
            devices.Add(node);
        }

        // Recursively check children
        foreach (var child in node.Children)
        {
            CollectDeviceNodes(child, devices);
        }
    }

    /// <summary>
    /// Pull data for a single device for entire month, organized by day
    /// </summary>
    private DeviceTrendData PullDeviceDataForMonth(string deviceName, string tagName, int month, int year)
    {
        try
        {
            var deviceData = new DeviceTrendData
            {
                DeviceName = deviceName,
                TagName = tagName
            };

            // Get date range for the month
            DateTime startDate = new DateTime(year, month, 1);
            int daysInMonth = DateTime.DaysInMonth(year, month);
            DateTime endDate = new DateTime(year, month, daysInMonth).AddDays(1).AddSeconds(-1);

            // Build SQL query for this device and tag for entire month
            string where = $"{Q("DeviceName")} = '{deviceName.Replace("'", "''")}' " +
                          $"AND {Q("Timestamp")} >= '{startDate:O}' " +
                          $"AND {Q("Timestamp")} < '{endDate:O}' ";

            string sourceTable = IsEnergyLogTag(tagName) ? TABLE_ENERGY : TABLE;
            string sql = $"SELECT {Q("Timestamp")}, {Q(tagName)} FROM {Q(sourceTable)} WHERE {where} ORDER BY {Q("Timestamp")}";

            dataStore.Query(sql, out _, out object[,] rows);

            if (rows == null || rows.GetLength(0) == 0)
            {
                return deviceData;
            }

            // Process rows and organize by day
            int rowCount = rows.GetLength(0);
            for (int i = 0; i < rowCount; i++)
            {
                try
                {
                    var tsObj = rows[i, 0];
                    var vObj = rows[i, 1];

                    DateTime ts = ParseDbTimestamp(tsObj);
                    double? val = TryParseDouble(vObj);

                    if (ts == DateTime.MinValue || val == null)
                        continue;

                    int dayOfMonth = ts.Day;

                    // Create daily data point
                    var point = new DailyTrendPoint
                    {
                        Timestamp = ts,
                        Value = val.Value,
                        Hour = ts.Hour,
                        Minute = ts.Minute
                    };

                    // Add to daily data dictionary
                    if (!deviceData.DailyData.ContainsKey(dayOfMonth))
                    {
                        deviceData.DailyData[dayOfMonth] = new List<DailyTrendPoint>();
                    }

                    deviceData.DailyData[dayOfMonth].Add(point);
                }
                catch (Exception ex)
                {
                    Log.Warning("Dashboard_CalendarTrendsAggregator", $"Error processing row {i}: {ex.Message}");
                }
            }

            return deviceData;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"Error pulling device data: {ex.Message}");
            return null;
        }
    }



    /// <summary>
    /// Initialize JSON file path for storing calendar trend data
    /// </summary>
    private void InitializeJsonPath()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string logsDir = Path.Combine(baseDirectory, "res/TabCommon_CalendarTrendsDashboard");

            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);

            jsonDataPath = Path.Combine(logsDir, "CalendarTrends_Data.json");
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"Error initializing JSON path: {ex.Message}");
        }
    }

    /// <summary>
    /// Load calendar trend data from JSON file
    /// </summary>
    private CalendarTrendData LoadCalendarTrendData()
    {
        try
        {
            if (File.Exists(jsonDataPath))
            {
                string json = File.ReadAllText(jsonDataPath);
                return JsonSerializer.Deserialize<CalendarTrendData>(json) ?? new CalendarTrendData();
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_CalendarTrendsAggregator", $"Error loading JSON: {ex.Message}");
        }

        return new CalendarTrendData();
    }

    /// <summary>
    /// Save calendar trend data to JSON file
    /// </summary>
    private void SaveCalendarTrendData(CalendarTrendData data)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(jsonDataPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"Error saving JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Get calendar trend data file path (for UI reference)
    /// </summary>
    [ExportMethod]
    public string GetCalendarTrendDataFilePath()
    {
        return jsonDataPath;
    }



    /// <summary>
    /// Get number of days in month
    /// </summary>
    private int GetDaysInMonth(int month, int year)
    {
        return DateTime.DaysInMonth(year, month);
    }

    /// <summary>
    /// Get first day of week for month (0=Sunday, 6=Saturday)
    /// </summary>
    [ExportMethod]
    public int GetFirstDayOfMonth(int month, int year)
    {
        try
        {
            DateTime firstDay = new DateTime(year, month, 1);
            return (int)firstDay.DayOfWeek; // 0=Sunday
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetFirstDayOfMonth failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get number of days in month
    /// </summary>
    [ExportMethod]
    public int GetDaysInMonthForCombo(int month, int year)
    {
        try
        {
            return GetDaysInMonth(month, year);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetDaysInMonthForCombo failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Format date string
    /// </summary>
    [ExportMethod]
    public string FormatDateString(int day, int month, int year)
    {
        try
        {
            DateTime date = new DateTime(year, month, day);
            return date.ToString("yyyy-MM-dd");
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"FormatDateString failed: {ex.Message}");
            return "";
        }
    }



    /// <summary>
    /// Main export method: Called from dashboard Load button
    /// Takes values from UI comboboxes and pulls data
    /// 
    /// Usage from dashboard:
    /// - User selects: Node, Tag, Month, Year
    /// - Click "Load" button
    /// - Method pulls all data and saves to JSON
    /// - JavaScript UI loads and displays calendar
    /// </summary>
    [ExportMethod]
    public string LoadCalendarTrendDataFromUI(string selectedNodeName, string selectedTag, int month, int year)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(selectedNodeName))
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "No node selected");
                return "";
            }

            if (string.IsNullOrWhiteSpace(selectedTag))
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "No tag selected");
                return "";
            }

            // Call main data pull method
            string filePath = PullCalendarTrendData(selectedNodeName, selectedTag, month, year);

            if (string.IsNullOrEmpty(filePath))
            {
                Log.Error("Dashboard_CalendarTrendsAggregator", "Failed to generate calendar trend data");
                return "";
            }

            // Refresh browser to display newly loaded data
            RefreshCalendarDashboardBrowser();

            return filePath;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"LoadCalendarTrendDataFromUI failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Get JSON file path for HTML to load
    /// Returns the Web-accessible path
    /// </summary>
    [ExportMethod]
    public string GetCalendarTrendDataWebPath()
    {
        try
        {
            // Convert from project path to web-accessible path
            string webPath = jsonDataPath.Replace("\\", "/");

            // If it's in Logs folder, map to Web path
            if (webPath.Contains("/Logs/"))
            {
                webPath = webPath.Replace("/Logs/CalendarTrends_Data.json", "");
                webPath = webPath.Substring(0, webPath.LastIndexOf("/")) + "/Web/trend/CalendarTrends_Data.json";
            }

            return webPath;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetCalendarTrendDataWebPath failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Get count of available devices in selected node
    /// Useful for pre-loading info before pulling data
    /// </summary>
    [ExportMethod]
    public int GetDeviceCountInNode(string nodeNameOrPath)
    {
        try
        {
            var devices = GetDeviceNamesFromOrgNode(nodeNameOrPath);
            return devices.Count;
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_CalendarTrendsAggregator", $"GetDeviceCountInNode failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get last generated calendar trend data info
    /// Returns summary: "Generated: 12/18/2025 10:30:00, Devices: 5, Days: 28"
    /// </summary>
    [ExportMethod]
    public string GetCalendarTrendDataInfo()
    {
        try
        {
            if (File.Exists(jsonDataPath))
            {
                var fileInfo = new System.IO.FileInfo(jsonDataPath);
                string json = File.ReadAllText(jsonDataPath);
                var data = JsonSerializer.Deserialize<CalendarTrendData>(json);

                if (data != null)
                {
                    int deviceCount = data.Devices.Count;
                    int totalDays = data.Devices.Sum(d => d.DailyData.Count);

                    return $"Generated: {data.GeneratedAt:MM/dd/yyyy HH:mm:ss}, " +
                           $"Node: {data.SelectedNode}, Tag: {data.SelectedTag}, " +
                           $"Period: {data.SelectedMonth}/{data.SelectedYear}, " +
                           $"Devices: {deviceCount}, Days with Data: {totalDays}";
                }
            }

            return "No calendar trend data available yet";
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_CalendarTrendsAggregator", $"GetCalendarTrendDataInfo failed: {ex.Message}");
            return "Error reading data info";
        }
    }

    /// <summary>
    /// Clear calendar trend data (reset)
    /// </summary>
    [ExportMethod]
    public void ClearCalendarTrendData()
    {
        try
        {
            if (File.Exists(jsonDataPath))
            {
                File.Delete(jsonDataPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"Error clearing data: {ex.Message}");
        }
    }

    #region Navigation & Auto-Load Methods

    /// <summary>
    /// Navigate to NEXT month and update comboboxes
    /// Automatically triggers data reload
    /// 
    /// Bind to: Next Month Button → OnClicked event
    /// </summary>
    [ExportMethod]
    public void NavigateToNextMonth()
    {
        try
        {
            if (cb_Month == null || cb_Year == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Month or Year combobox not found");
                return;
            }

            // Get current month/year from combobox SelectedValue
            var monthVar = cb_Month.GetVariable("SelectedValue");
            var yearVar = cb_Year.GetVariable("SelectedValue");

            if (monthVar == null || yearVar == null) return;

            // Parse current values
            string monthStr = GetValueAsString(monthVar.Value?.Value);
            string yearStr = GetValueAsString(yearVar.Value?.Value);
            
            if (!int.TryParse(monthStr, out int currentMonth)) currentMonth = DateTime.Now.Month;
            if (!int.TryParse(yearStr, out int currentYear)) currentYear = DateTime.Now.Year;

            // Calculate next month
            int nextMonth = currentMonth + 1;
            int nextYear = currentYear;
            if (nextMonth > 12)
            {
                nextMonth = 1;
                nextYear++;
            }

            // Update combobox values
            monthVar.Value = nextMonth.ToString();
            yearVar.Value = nextYear.ToString();

            // Trigger data reload
            OnMonthYearChanged();
            
            // Refresh browser to display updated calendar
            RefreshCalendarDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"NavigateToNextMonth failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Navigate to PREVIOUS month and update comboboxes
    /// Automatically triggers data reload
    /// 
    /// Bind to: Previous Month Button → OnClicked event
    /// </summary>
    [ExportMethod]
    public void NavigateToPreviousMonth()
    {
        try
        {
            if (cb_Month == null || cb_Year == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Month or Year combobox not found");
                return;
            }

            // Get current month/year from combobox SelectedValue
            var monthVar = cb_Month.GetVariable("SelectedValue");
            var yearVar = cb_Year.GetVariable("SelectedValue");

            if (monthVar == null || yearVar == null) return;

            // Parse current values
            string monthStr = GetValueAsString(monthVar.Value?.Value);
            string yearStr = GetValueAsString(yearVar.Value?.Value);
            
            if (!int.TryParse(monthStr, out int currentMonth)) currentMonth = DateTime.Now.Month;
            if (!int.TryParse(yearStr, out int currentYear)) currentYear = DateTime.Now.Year;

            // Calculate previous month
            int prevMonth = currentMonth - 1;
            int prevYear = currentYear;
            if (prevMonth < 1)
            {
                prevMonth = 12;
                prevYear--;
            }

            // Update combobox values
            monthVar.Value = prevMonth.ToString();
            yearVar.Value = prevYear.ToString();

            // Trigger data reload
            OnMonthYearChanged();
            
            // Refresh browser to display updated calendar
            RefreshCalendarDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"NavigateToPreviousMonth failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when Tag selection changes
    /// Automatically reloads calendar data with new tag
    /// 
    /// Bind to: cb_Tag → OnUserSelectionChanged event
    /// </summary>
    [ExportMethod]
    public void OnTagChanged()
    {
        try
        {
            // Reload data with new tag (month/year remain the same)
            OnMonthYearChanged();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"OnTagChanged failed: {ex.Message}");
        }
    }

   
    [ExportMethod]
    public void OnMonthYearChanged()
    {
        try
        {
            // Get current selections from comboboxes
            var monthVar = cb_Month?.GetVariable("SelectedValue");
            var yearVar = cb_Year?.GetVariable("SelectedValue");

            if (monthVar == null || yearVar == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Required combobox variables not initialized");
                return;
            }

            // Get node from navigation tree (selectedOrgNodeVar)
            string node = selectedOrgNodeVar?.Value?.Value?.ToString() ?? "";
            
            // Get tag directly from combobox SelectedValue as LocalizedText
            string tag = "";
            try
            {
                var tagValue = cb_Tag?.GetVariable("SelectedValue")?.Value?.Value;
                if (tagValue is UAManagedCore.LocalizedText localizedText)
                {
                    tag = localizedText.Text;
                }
                else if (tagValue != null)
                {
                    tag = tagValue.ToString();
                }
            }
            catch { }
            
            // Parse month and year from combobox SelectedValue
            string monthStr = GetValueAsString(monthVar.Value?.Value);
            string yearStr = GetValueAsString(yearVar.Value?.Value);
           
            if (!int.TryParse(monthStr, out int month))
                month = DateTime.Now.Month;
            if (!int.TryParse(yearStr, out int year))
                year = DateTime.Now.Year;

            if (string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(tag))
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Node or Tag not selected");
                return;
            }

            // Automatically pull data
            PullCalendarTrendData(node, tag, month, year);
            RefreshCalendarDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"OnMonthYearChanged failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialize calendar with data based on currently selected org node
    /// Call this when tab opens for the first time
    /// 
    /// Bind to: Tab → OnLoaded event
    /// </summary>
    [ExportMethod]
    public void InitializeWithSelectedNode()
    {
        try
        {
            // Use the already-subscribed selectedOrgNodeVar from Start()
            string currentPath = selectedOrgNodeVar?.Value?.Value?.ToString() ?? "";
            
            if (string.IsNullOrEmpty(currentPath))
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "No org node selected. Please select a node from the navigation tree.");
            }

            // Set default month/year
            var monthVar = cb_Month?.GetVariable("SelectedValue");
            var yearVar = cb_Year?.GetVariable("SelectedValue");
            if (monthVar != null) monthVar.Value = DateTime.Now.Month;
            if (yearVar != null) yearVar.Value = DateTime.Now.Year;

            // Auto-load data if node is selected
            if (!string.IsNullOrEmpty(currentPath))
            {
                OnMonthYearChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"InitializeWithSelectedNode failed: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// Get list of org nodes for dropdown population
    /// Returns object array with node names
    /// </summary>
    [ExportMethod]
    public object[,] GetAvailableOrgNodes()
    {
        try
        {
            var nodes = new List<string>();

            var modelNode = Project.Current.Get("Model");
            if (modelNode != null)
            {
                var orgModelNode = modelNode.Get("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel");
                if (orgModelNode != null)
                {
                    // Recursively collect org nodes
                    CollectOrgNodes(orgModelNode as UANode, ref nodes);
                }
            }

            if (nodes.Count == 0)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "No org nodes found");
                return new object[0, 0];
            }

            object[,] result = new object[nodes.Count, 1];
            for (int i = 0; i < nodes.Count; i++)
            {
                result[i, 0] = nodes[i];
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_CalendarTrendsAggregator", $"GetAvailableOrgNodes failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Recursively collect org node names
    /// </summary>
    private void CollectOrgNodes(UANode node, ref List<string> nodes)
    {
        if (node == null) return;

        // Add non-device nodes (org structure)
        if (!node.BrowseName.Contains("PM5000"))
        {
            if (!nodes.Contains(node.BrowseName))
            {
                nodes.Add(node.BrowseName);
            }
        }

        try
        {
            var children = node.Children;
            if (children != null)
            {
                foreach (var child in children)
                {
                    CollectOrgNodes(child as UANode, ref nodes);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Get device names from a selected org node path
    /// Uses the same pattern as Energy_LogAggregator
    /// Navigates to the org node and collects all PM5000 devices underneath
    /// </summary>
    private List<string> GetDeviceNamesFromOrgNode(string orgNodePath)
    {
        var devices = new List<string>();

        try
        {
            if (string.IsNullOrEmpty(orgNodePath))
            {
                return devices;
            }

            // Navigate to specific org node
            var orgNode = Project.Current.Get(orgNodePath);
            if (orgNode == null)
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", $"Org node not found: {orgNodePath}");
                return devices;
            }

            // Collect device nodes under this org node
            var deviceNodes = new List<IUANode>();
            CollectDeviceNodes(orgNode, deviceNodes);

            // Extract device names
            foreach (var deviceNode in deviceNodes)
            {
                if (deviceNode != null && !devices.Contains(deviceNode.BrowseName))
                {
                    devices.Add(deviceNode.BrowseName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_CalendarTrendsAggregator", $"Error getting device names from org node: {ex.Message}");
        }

        return devices;
    }

   
    private void RefreshCalendarDashboardBrowser()
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("wb_CalendarTrends");
            
            if (webBrowser != null)
            {
                webBrowser.Refresh();
            }
            else
            {
                Log.Warning("Dashboard_CalendarTrendsAggregator", "Calendar dashboard WebBrowser not found for refresh");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_CalendarTrendsAggregator", $"Error refreshing calendar dashboard: {ex.Message}");
        }
    }

    
    /// <summary>
    /// Safely extract value from UAValue or complex object
    /// Priority: Value property (actual stored value) > Text property (display name) > ToString()
    /// </summary>
    private string GetValueAsString(object uaValue)
    {
        if (uaValue == null)
            return "";

        var objType = uaValue.GetType();

        // First try to get the Value property (the actual stored value, not display text)
        var valueProp = objType.GetProperty("Value");
        if (valueProp != null)
        {
            var value = valueProp.GetValue(uaValue)?.ToString();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        // Then try the Text property (for display purposes)
        var textProp = objType.GetProperty("Text");
        if (textProp != null)
        {
            var text = textProp.GetValue(uaValue)?.ToString();
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        // Fallback to ToString()
        return uaValue.ToString() ?? "";
    }

    private string StripModelPrefix(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return "";

        const string prefix = "Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel/";
        if (fullPath.StartsWith(prefix))
            return fullPath.Substring(prefix.Length);

        return fullPath;
    }

    private string Q(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "\"\"";
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private bool IsEnergyLogTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return false;
        return ENERGY_LOG_COLUMNS.Any(c => string.Equals(c, tagName, StringComparison.OrdinalIgnoreCase));
    }

    
    private string SanitizeNodeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var safe = new string(raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (char.IsDigit(safe[0])) safe = "_" + safe;
        return safe;
    }

    /// <summary>
    /// Convert database column name to user-friendly display name
    /// Examples:
    ///   "kW_Demand" → "kW Demand"
    ///   "Total_kWh_Fwd" → "Total kWh Fwd"
    ///   "Projected_kVAR_Demand" → "Projected kVAR Demand"
    ///   "XYZ_XYZ_%" → "XYZ XYZ"
    /// </summary>
    private string FormatDisplayName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return "Unknown";

        try
        {
            // Remove leading/trailing special characters (like underscores, %, etc.)
            string cleaned = columnName.Trim('_', '%', '-', '.', ' ');

            // Replace underscores with spaces
            string formatted = cleaned.Replace("_", " ");

            // Remove multiple consecutive spaces
            var regex = new System.Text.RegularExpressions.Regex(@"\s+");
            formatted = regex.Replace(formatted, " ");

            return formatted.Trim();
        }
        catch
        {
            return columnName;
        }
    }

    
    private T FindFirstByName<T>(IUANode root, string name) where T : class, IUANode
    {
        if (root == null || string.IsNullOrWhiteSpace(name)) return null;
        var q = new Queue<IUANode>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (string.Equals(n?.BrowseName, name, StringComparison.Ordinal))
                return n as T;
            foreach (var c in n.Children)
                q.Enqueue(c);
        }
        return null;
    }

   
    private IUAVariable FindVariableByName(IUANode root, string name)
    {
        if (root == null || string.IsNullOrWhiteSpace(name)) return null;
        var q = new Queue<IUANode>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (n is IUAVariable v && string.Equals(v.BrowseName, name, StringComparison.Ordinal))
                return v;
            foreach (var c in n.Children)
                q.Enqueue(c);
        }
        return null;
    }


}



public class CalendarTrendData
{
    [JsonPropertyName("selectedNode")]
    public string SelectedNode { get; set; }

    [JsonPropertyName("selectedTag")]
    public string SelectedTag { get; set; }

    [JsonPropertyName("selectedMonth")]
    public int SelectedMonth { get; set; }

    [JsonPropertyName("selectedYear")]
    public int SelectedYear { get; set; }

    [JsonPropertyName("devices")]
    public List<DeviceTrendData> Devices { get; set; } = new List<DeviceTrendData>();

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; }
}

/// <summary>
/// Device trend data for a specific tag and month
/// </summary>
public class DeviceTrendData
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; }

    [JsonPropertyName("tagName")]
    public string TagName { get; set; }

    [JsonPropertyName("dailyData")]
    public Dictionary<int, List<DailyTrendPoint>> DailyData { get; set; } = new Dictionary<int, List<DailyTrendPoint>>();
}

/// <summary>
/// Single data point for a day
/// </summary>
public class DailyTrendPoint
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("hour")]
    public int Hour { get; set; }

    [JsonPropertyName("minute")]
    public int Minute { get; set; }
}
#endregion
