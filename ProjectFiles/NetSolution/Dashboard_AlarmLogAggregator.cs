using System;
using System.Collections.Generic;
using System.IO;
using OpcUa = UAManagedCore.OpcUa;
using System.Linq;
using System.Text.Json;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.UI;
using FTOptix.SerialPort;


public class Dashboard_AlarmLogAggregator : BaseNetLogic
{
    private IUAVariable selectedOrgNodeVar;
    private IUAVariable startDateVar;  // UI selector for start date
    private IUAVariable endDateVar;    // UI selector for end date
    private IUAVariable selectedDaysVar;  // Tracks selected days for button highlighting
    private IUAVariable errorMessageVar;  // Displays error messages for invalid date ranges
    private Store commonStore;

    // Table name
    private const string ALARM_LOG_TABLE = "AlarmLog_AllDevices";
    
    // Time range filter variables
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-30); // Default: last 30 days
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 30;

    public override void Start()
    {
        // Get reference to SelectedOrgNodePath variable from parent NavigationPanelright
        try
        {
            var navigationPanel = Owner.Owner.Owner; // Navigate up to NavigationPanelright
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            
            if (selectedOrgNodeVar == null)
            {
                Log.Warning("Dashboard_AlarmLogAggregator", "SelectedOrgNodePath variable not found in NavigationPanelright, creating local copy");
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
            Log.Warning("Dashboard_AlarmLogAggregator", $"Error getting SelectedOrgNodePath: {ex.Message}");
        }

        // Get UI date selector variables
        try
        {
            startDateVar = Owner.GetVariable("StartDate");
            endDateVar = Owner.GetVariable("EndDate");
            selectedDaysVar = Owner.GetVariable("SelectedDays");
            errorMessageVar = Owner.GetVariable("ErrorMessage");
            
            if (startDateVar != null && endDateVar != null)
            {
                startDateVar.Value = timeRangeStartDate;
                endDateVar.Value = timeRangeEndDate;
            }
            
            // Initialize SelectedDays with default (30 days)
            if (selectedDaysVar != null)
                selectedDaysVar.Value = timeRangeDays;
            
            // Clear any error messages on start
            if (errorMessageVar != null)
                errorMessageVar.Value = "";
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_AlarmLogAggregator", $"Error getting date variables: {ex.Message}");
        }

        // Get common database store reference
        try
        {
            commonStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (commonStore == null)
            {
                Log.Error("Dashboard_AlarmLogAggregator", "Common store not found!");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", $"Failed to get common store: {ex.Message}");
            return;
        }

        // Load initial data if org is already selected
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            if (!string.IsNullOrEmpty(selectedPath))
            {
                LoadAlarmsForDays(timeRangeDays);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_AlarmLogAggregator", "Error loading initial alarm data: " + ex.Message);
        }
    }

    public override void Stop()
    {
        if (selectedOrgNodeVar != null)
        {
            selectedOrgNodeVar.VariableChange -= OnSelectedOrgNodeChanged;
        }
    }

    private void OnSelectedOrgNodeChanged(object sender, UAManagedCore.VariableChangeEventArgs args)
    {
        AggregateAlarmData(null);
    }

    private void AggregateAlarmData(PeriodicTask task)
    {
        try
        {
            string selectedOrgPath = selectedOrgNodeVar?.Value ?? "";

            // Format time range description
            string timeRangeDescription = FormatTimeRangeDescription();

            // Collect all alarm data
            var alarmData = new
            {
                timestamp = DateTime.Now.ToString("o"),
                orgNodePath = StripModelPrefix(selectedOrgPath),
                orgNodeName = GetOrgNodeName(selectedOrgPath),
                deviceCount = GetDeviceCountInOrgNode(selectedOrgPath),

                // Time range info (for dashboard display)
                timeRange = new
                {
                    startDate = timeRangeStartDate.ToString("yyyy-MM-dd HH:mm:ss"),
                    endDate = timeRangeEndDate.ToString("yyyy-MM-dd HH:mm:ss"),
                    description = timeRangeDescription,
                    days = timeRangeDays
                },

                // Main KPIs
                totalAlarms = GetTotalAlarmsData(selectedOrgPath),
                alarmsBySeverity = GetAlarmsBySeverityData(selectedOrgPath),
                topAlarmDevice = GetTopAlarmDeviceData(selectedOrgPath),

                // Charts data
                alarmsByType = GetAlarmsByTypeData(selectedOrgPath),
                alarmTrend = GetAlarmTrendData(selectedOrgPath),

                // Detail table
                recentAlarms = GetRecentAlarmsData(selectedOrgPath),

                // Additional metadata
                lastUpdated = DateTime.Now.ToString("HH:mm:ss")
            };

            // Write to JSON file
            WriteAlarmJSON(alarmData);
            
            // Refresh the WebBrowser to display updated data
            RefreshAlarmDashboardBrowser();

        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", $"Error aggregating alarm data: {ex.Message}");
        }
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
        
    

    #region Total Alarms

    private object GetTotalAlarmsData(string orgPath)
    {
        try
        {
            if (commonStore == null)
                return CreateEmptyAlarmData();

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
                return CreateEmptyAlarmData();

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            var deviceFilter = string.Join("','", deviceNames);

            // Count total alarms from time range
            string query = $@"
                SELECT COUNT(*) as AlarmCount 
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Timestamp >= '{timeRangeStartDate:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp <= '{timeRangeEndDate:yyyy-MM-ddTHH:mm:ss}'";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            int totalCount = 0;
            if (result != null && result.GetLength(0) > 0 && result[0, 0] != null)
            {
                totalCount = Convert.ToInt32(result[0, 0]);
            }

            // Get previous period count for trend comparison
            int prevPeriodDays = Math.Max(timeRangeDays, 1);
            DateTime prevPeriodStart = timeRangeStartDate > DateTime.MinValue.AddDays(prevPeriodDays) 
                ? timeRangeStartDate.AddDays(-prevPeriodDays) 
                : DateTime.MinValue;
            DateTime prevPeriodEnd = timeRangeStartDate;

            string prevQuery = $@"
                SELECT COUNT(*) as AlarmCount 
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Timestamp >= '{prevPeriodStart:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp < '{prevPeriodEnd:yyyy-MM-ddTHH:mm:ss}'";

            commonStore.Query(prevQuery, out columns, out result);

            int prevCount = 0;
            if (result != null && result.GetLength(0) > 0 && result[0, 0] != null)
            {
                prevCount = Convert.ToInt32(result[0, 0]);
            }

            double changePercent = prevCount > 0
                ? ((totalCount - prevCount) / (double)prevCount) * 100
                : 0;

            string trend = changePercent >= 0 ? "up" : "down";
            string changeText = changePercent >= 0
                ? "+" + changePercent.ToString("F1") + "%"
                : changePercent.ToString("F1") + "%";

            return new
            {
                value = totalCount,
                change = changeText,
                trend = trend,
                subtext = "alarms (last 30 days)"
            };
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", "Error calculating total alarms: " + ex.Message);
            return CreateEmptyAlarmData();
        }
    }

    private object CreateEmptyAlarmData()
    {
        return new
        {
            value = 0,
            change = "0%",
            trend = "neutral",
            subtext = "No data"
        };
    }

    #endregion

    #region Alarms by Severity

    private object GetAlarmsBySeverityData(string orgPath)
    {
        try
        {
            if (commonStore == null)
                return CreateEmptySeverityData();

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
                return CreateEmptySeverityData();

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            var deviceFilter = string.Join("','", deviceNames);

            string query = $@"
                SELECT Severity, COUNT(*) as Count 
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Timestamp >= '{timeRangeStartDate:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp <= '{timeRangeEndDate:yyyy-MM-ddTHH:mm:ss}'
                GROUP BY Severity";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            int critical = 0, warning = 0, info = 0;

            if (result != null)
            {
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    string severity = result[i, 0]?.ToString() ?? "";
                    int count = result[i, 1] != null ? Convert.ToInt32(result[i, 1]) : 0;

                    if (severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
                        critical = count;
                    else if (severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                        warning = count;
                    else if (severity.Equals("Info", StringComparison.OrdinalIgnoreCase))
                        info = count;
                }
            }

            int total = critical + warning + info;

            return new
            {
                total = total,
                critical = critical,
                warning = warning,
                info = info,
                subtext = $"in range: {timeRangeStartDate:MMM dd} to {timeRangeEndDate:MMM dd}"
            };
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", "Error calculating alarms by severity: " + ex.Message);
            return CreateEmptySeverityData();
        }
    }

    private object CreateEmptySeverityData()
    {
        return new
        {
            total = 0,
            critical = 0,
            warning = 0,
            info = 0,
            subtext = "No data"
        };
    }

    #endregion

    #region Top Alarm Device

    private object GetTopAlarmDeviceData(string orgPath)
    {
        try
        {
            if (commonStore == null)
                return new { device = "N/A", count = 0 };

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
                return new { device = "N/A", count = 0 };

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            var deviceFilter = string.Join("','", deviceNames);

            string query = $@"
                SELECT DeviceName, COUNT(*) as AlarmCount 
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Timestamp >= '{timeRangeStartDate:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp <= '{timeRangeEndDate:yyyy-MM-ddTHH:mm:ss}'
                GROUP BY DeviceName
                ORDER BY AlarmCount DESC
                LIMIT 1";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            if (result != null && result.GetLength(0) > 0)
            {
                string deviceName = result[0, 0]?.ToString() ?? "N/A";
                int count = result[0, 1] != null ? Convert.ToInt32(result[0, 1]) : 0;
                return new { device = deviceName, count = count };
            }

            return new { device = "N/A", count = 0 };
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", "Error calculating top alarm device: " + ex.Message);
            return new { device = "N/A", count = 0 };
        }
    }

    #endregion

    #region Alarms by Type

    private object GetAlarmsByTypeData(string orgPath)
    {
        try
        {
            if (commonStore == null)
                return new List<object>();

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
                return new List<object>();

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            var deviceFilter = string.Join("','", deviceNames);

            string query = $@"
                SELECT AlarmTypeDescription, COUNT(*) as Count 
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Timestamp >= '{timeRangeStartDate:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp <= '{timeRangeEndDate:yyyy-MM-ddTHH:mm:ss}'
                GROUP BY AlarmTypeDescription
                ORDER BY Count DESC
                LIMIT 10";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            var typeData = new List<object>();

            if (result != null)
            {
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    typeData.Add(new
                    {
                        type = result[i, 0]?.ToString() ?? "Unknown",
                        count = result[i, 1] != null ? Convert.ToInt32(result[i, 1]) : 0
                    });
                }
            }

            return typeData;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", "Error getting alarms by type: " + ex.Message);
            return new List<object>();
        }
    }

    #endregion

    #region Alarm Trend

    private object GetAlarmTrendData(string orgPath)
    {
        try
        {
            if (commonStore == null)
                return new { data = new List<object>() };

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
                return new { data = new List<object>() };

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            var deviceFilter = string.Join("','", deviceNames);

            // Simple query - get all alarms, grouping done in C#
            string query = $@"
                SELECT Timestamp
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Timestamp >= '{timeRangeStartDate:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp <= '{timeRangeEndDate:yyyy-MM-ddTHH:mm:ss}'
                ORDER BY Timestamp";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            // Group by date in C#
            var trendDataDict = new Dictionary<string, int>();

            if (result != null)
            {
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    string timestamp = result[i, 0]?.ToString() ?? "";
                    // Extract date part (DD-MM-YYYY) from timestamp
                    string dateStr = timestamp.Length >= 10 ? timestamp.Substring(0, 10) : timestamp;
                    
                    if (!trendDataDict.ContainsKey(dateStr))
                        trendDataDict[dateStr] = 0;
                    trendDataDict[dateStr]++;
                }
            }

            // Sort by parsing dates properly (handle DD-MM-YYYY format)
            var trendData = trendDataDict
                .OrderBy(kvp => {
                    try
                    {
                        // Parse DD-MM-YYYY format
                        var parts = kvp.Key.Split('-');
                        if (parts.Length == 3)
                        {
                            int day = int.Parse(parts[0]);
                            int month = int.Parse(parts[1]);
                            int year = int.Parse(parts[2]);
                            return new DateTime(year, month, day);
                        }
                        return DateTime.MinValue;
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .Select(kvp => new { date = kvp.Key, count = kvp.Value })
                .ToList<object>();

            return new { data = trendData };
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", "Error getting alarm trend: " + ex.Message);
            return new { data = new List<object>() };
        }
    }

    #endregion

    #region Recent Alarms

    private object GetRecentAlarmsData(string orgPath)
    {
        try
        {
            if (commonStore == null)
                return new List<object>();

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
                return new List<object>();

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            var deviceFilter = string.Join("','", deviceNames);

            string query = $@"
                SELECT 
                    Timestamp,
                    DeviceName,
                    AlarmTypeDescription,
                    AlarmCode,
                    AlarmCodeDescription,
                    Severity
                FROM {ALARM_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}')
                    AND Timestamp >= '{timeRangeStartDate:yyyy-MM-ddTHH:mm:ss}' 
                    AND Timestamp <= '{timeRangeEndDate:yyyy-MM-ddTHH:mm:ss}'
                ORDER BY Timestamp DESC
                LIMIT 100";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            var recentAlarms = new List<object>();

            if (result != null)
            {
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    recentAlarms.Add(new
                    {
                        timestamp = result[i, 0]?.ToString() ?? "",
                        device = result[i, 1]?.ToString() ?? "",
                        type = result[i, 2]?.ToString() ?? "",
                        code = result[i, 3]?.ToString() ?? "",
                        message = result[i, 4]?.ToString() ?? "",
                        severity = result[i, 5]?.ToString() ?? ""
                    });
                }
            }

            return recentAlarms;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", "Error getting recent alarms: " + ex.Message);
            return new List<object>();
        }
    }

    #endregion

    #region Helper Methods

    private List<IUANode> GetDevicesInOrgNode(string orgPath)
    {
        var devices = new List<IUANode>();

        if (string.IsNullOrEmpty(orgPath))
        {
            var modelFolder = Project.Current.Get("Model");
            if (modelFolder != null)
            {
                CollectDeviceNodes(modelFolder, devices);
            }
            return devices;
        }

        var orgNode = Project.Current.Get(orgPath);
        if (orgNode == null)
        {
            Log.Warning("Dashboard_AlarmLogAggregator", $"Org node not found: {orgPath}");
            return devices;
        }

        CollectDeviceNodes(orgNode, devices);
        return devices;
    }

    private void CollectDeviceNodes(IUANode node, List<IUANode> devices)
    {
        // Check if this node is a device (has DeviceAlarms node)
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

    private string GetOrgNodeName(string orgPath)
    {
        if (string.IsNullOrEmpty(orgPath))
        {
            return "All Devices";
        }

        var parts = orgPath.Split('/');
        return parts.Length > 0 ? parts[parts.Length - 1] : "Unknown";
    }

    private int GetDeviceCountInOrgNode(string orgPath)
    {
        var devices = GetDevicesInOrgNode(orgPath);
        return devices.Count;
    }

    private void WriteAlarmJSON(object data)
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res/TabCommon_AlarmTypesDashboard");
            string filePath = Path.Combine(directory, "AlarmLog_Data.json");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string jsonString = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(filePath, jsonString);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", $"Failed to save alarm JSON: {ex.Message}");
        }
    }

    private string FormatTimeRangeDescription()
    {
        if (timeRangeDays == 1)
            return "Last 24 Hours";
        else if (timeRangeDays == 7)
            return "Last 7 Days";
        else if (timeRangeDays == 30)
            return "Last 30 Days";
        else if (timeRangeDays == 0)
            return "Today";
        else
            return $"{timeRangeDays} Days ({timeRangeStartDate:MMM dd} to {timeRangeEndDate:MMM dd})";
    }

    #endregion

    #region Exported Methods

    [ExportMethod]
    public void RefreshAlarmLog()
    {
        AggregateAlarmData(null);
    }

    /// <summary>
    /// Set time range using number of days (generic method)
    /// </summary>
    [ExportMethod]
    public void LoadAlarmsForDays(int days)
    {
        if (days < 0)
        {
            Log.Warning("Dashboard_AlarmLogAggregator", "Days must be >= 0");
            return;
        }

        DateTime endDate = DateTime.Now;
        DateTime startDate = endDate.AddDays(-days);
        
        timeRangeStartDate = startDate;
        timeRangeEndDate = endDate;
        timeRangeDays = days;
        
        if (startDateVar != null) startDateVar.Value = timeRangeStartDate;
        if (endDateVar != null) endDateVar.Value = timeRangeEndDate;
        if (selectedDaysVar != null) selectedDaysVar.Value = days;
        if (errorMessageVar != null) errorMessageVar.Value = "";
        
        AggregateAlarmData(null);
    }

    /// <summary>
    /// Load alarms for last 24 hours
    /// </summary>
    [ExportMethod]
    public void LoadAlarms24Hours()
    {
        LoadAlarmsForDays(1);
    }

    /// <summary>
    /// Load alarms for last 7 days
    /// </summary>
    [ExportMethod]
    public void LoadAlarms7Days()
    {
        LoadAlarmsForDays(7);
    }

    /// <summary>
    /// Load alarms for last 30 days
    /// </summary>
    [ExportMethod]
    public void LoadAlarms30Days()
    {
        LoadAlarmsForDays(30);
    }

    /// <summary>
    /// Set custom date range for alarms
    /// </summary>
    [ExportMethod]
    public void LoadAlarmsCustomRange(DateTime startDate, DateTime endDate)
    {
        if (startDate >= endDate)
        {
            string errorMsg = "Error: Start date must be before end date";
            Log.Warning("Dashboard_AlarmLogAggregator", errorMsg);
            
            // Display error message to user
            if (errorMessageVar != null)
                errorMessageVar.Value = errorMsg;
            
            // Load blank data to clear dashboard
            WriteBlankAlarmData();
            RefreshAlarmDashboardBrowser();
            return;
        }

        timeRangeStartDate = startDate;
        timeRangeEndDate = endDate;
        timeRangeDays = (int)(endDate - startDate).TotalDays;
        
        if (startDateVar != null) startDateVar.Value = timeRangeStartDate;
        if (endDateVar != null) endDateVar.Value = timeRangeEndDate;
        if (selectedDaysVar != null) selectedDaysVar.Value = -1;
        if (errorMessageVar != null) errorMessageVar.Value = "";
        
        AggregateAlarmData(null);
    }

    private void WriteBlankAlarmData()
    {
        try
        {
            string selectedOrgPath = selectedOrgNodeVar?.Value ?? "";
            
            var blankData = new
            {
                timestamp = DateTime.Now.ToString("o"),
                orgNodePath = StripModelPrefix(selectedOrgPath),
                orgNodeName = GetOrgNodeName(selectedOrgPath),
                deviceCount = GetDeviceCountInOrgNode(selectedOrgPath),

                // Time range info (for dashboard display)
                timeRange = new
                {
                    startDate = "",
                    endDate = "",
                    description = "Invalid date range",
                    days = 0
                },

                // Empty KPIs
                totalAlarms = CreateEmptyAlarmData(),
                alarmsBySeverity = CreateEmptySeverityData(),
                topAlarmDevice = new { device = "N/A", count = 0 },

                // Empty charts data
                alarmsByType = new List<object>(),
                alarmTrend = new { data = new List<object>() },

                // Empty detail table
                recentAlarms = new List<object>(),

                // Additional metadata
                lastUpdated = DateTime.Now.ToString("HH:mm:ss")
            };

            WriteAlarmJSON(blankData);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_AlarmLogAggregator", $"Error writing blank alarm data: {ex.Message}");
        }
    }

    #endregion

    #region Dashboard Browser Refresh


    private void RefreshAlarmDashboardBrowser()
    {
        try
        {
           
            var webBrowser = Owner.Get<WebBrowser>("wb_AlarmTypesDashboard");
            

            if (webBrowser != null)
            {
                webBrowser.Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_AlarmLogAggregator", $"Error refreshing dashboard browser: {ex.Message}");
        }
    }

    #endregion
}
