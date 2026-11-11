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
using System.Globalization;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;


public class Dashboard_PQEventsLogAggregator : BaseNetLogic
{
    private IUAVariable selectedOrgNodeVar;
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable selectedDaysVar;
    private IUAVariable errorMessageVar;
    private Store commonStore;
    private WebBrowser pqEventsDashboardBrowser;
    private Dictionary<string, IUAVariable> newEventTriggerSubscriptions = new Dictionary<string, IUAVariable>();

    private const string PQ_LOG_TABLE = "PowerQualityLog_AllDevices";
    
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-30);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 30;

    public override void Start()
    {
        try
        {
            var navigationPanel = Owner.Owner.Owner;
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            
            if (selectedOrgNodeVar == null)
            {
                Log.Warning("PQEvents_LogAggregator", "SelectedOrgNodePath variable not found");
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
            Log.Warning("PQEvents_LogAggregator", $"Error getting SelectedOrgNodePath: {ex.Message}");
        }

        try
        {
            startDateVar = Owner.GetVariable("StartDate");
            endDateVar = Owner.GetVariable("EndDate");
            selectedDaysVar = Owner.GetVariable("SelectedDays");
            errorMessageVar = Owner.GetVariable("ErrorMessage");
            
            if (errorMessageVar == null)
            {
                errorMessageVar = InformationModel.MakeVariable("ErrorMessage", OpcUa.DataTypes.String);
                Owner.Add(errorMessageVar);
            }
            
            if (startDateVar != null && endDateVar != null)
            {
                startDateVar.Value = timeRangeStartDate;
                endDateVar.Value = timeRangeEndDate;
            }
            
            if (selectedDaysVar != null)
                selectedDaysVar.Value = timeRangeDays;
                
            if (errorMessageVar != null)
                errorMessageVar.Value = "";
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", $"Error getting date variables: {ex.Message}");
        }

        try
        {
            commonStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (commonStore == null)
            {
                Log.Error("PQEvents_LogAggregator", "Common store not found!");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("PQEvents_LogAggregator", $"Failed to get common store: {ex.Message}");
            return;
        }

        // Subscribe to NewEventTrigger for all devices
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            SubscribeToNewEventTriggers(selectedPath);
            
            // Load initial data if org is already selected
            if (!string.IsNullOrEmpty(selectedPath))
            {
                LoadPQEventsForDays(timeRangeDays);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error subscribing to event triggers: " + ex.Message);
        }
    }

    public override void Stop()
    {
        if (selectedOrgNodeVar != null)
        {
            selectedOrgNodeVar.VariableChange -= OnSelectedOrgNodeChanged;
        }

        // Unsubscribe from all NewEventTrigger variables
        UnsubscribeFromNewEventTriggers();
    }

    private void OnSelectedOrgNodeChanged(object sender, VariableChangeEventArgs e)
    {
        string selectedPath = selectedOrgNodeVar?.Value ?? "";
        
        // Unsubscribe from old org's triggers
        UnsubscribeFromNewEventTriggers();
        
        // Subscribe to new org's triggers
        SubscribeToNewEventTriggers(selectedPath);
        
        // Refresh data
        LoadPQEventsForDays(timeRangeDays);
    }

    [ExportMethod]
    public void LoadPQEvents24Hours() { LoadPQEventsForDays(1); }

    [ExportMethod]
    public void LoadPQEvents7Days() { LoadPQEventsForDays(7); }

    [ExportMethod]
    public void LoadPQEvents30Days() { LoadPQEventsForDays(30); }

    [ExportMethod]
    public void LoadPQEventsForDays(int days)
    {
        try
        {
            // Clear any previous error messages
            if (errorMessageVar != null)
                errorMessageVar.Value = "";
            
            DateTime endDate = DateTime.Now;
            DateTime startDate = endDate.AddDays(-days);
            
            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;
            timeRangeDays = days;
            
            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = days;
            
            AggregatePQEventData(startDate, endDate, days);
            RefreshPQEventsDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("PQEvents_LogAggregator", "Error loading PQ events: " + ex.Message);
        }
    }

    [ExportMethod]
    public void LoadPQEventsCustomRange(DateTime startDate, DateTime endDate)
    {
        try
        {
            // Validate that start date is not after end date
            if (startDate > endDate)
            {
                string errorMsg = $"Invalid date range: Start date ({startDate:yyyy-MM-dd}) cannot be after End date ({endDate:yyyy-MM-dd})";
                
                if (errorMessageVar != null)
                    errorMessageVar.Value = errorMsg;
                
                Log.Warning("PQEvents_LogAggregator", errorMsg);
                return;
            }
            
            // Clear any previous error messages
            if (errorMessageVar != null)
                errorMessageVar.Value = "";
            
            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;
            
            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = -1;
            
            int days = (int)Math.Round((endDate - startDate).TotalDays);
            AggregatePQEventData(startDate, endDate, days);
            RefreshPQEventsDashboardBrowser();
        }
        catch (Exception ex)
        {
            string errorMsg = "Error loading custom range: " + ex.Message;
            if (errorMessageVar != null)
                errorMessageVar.Value = errorMsg;
            
            Log.Error("PQEvents_LogAggregator", errorMsg);
        }
    }

    private void AggregatePQEventData(DateTime startDate, DateTime endDate, int daysInRange)
    {
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            var deviceNames = GetDeviceNamesFromOrgNode(selectedPath);
            
            if (deviceNames.Count == 0)
            {
                WriteEmptyPQEventsDashboard();
                return;
            }

            var summaryData = GetPQEventsSummary(deviceNames, startDate, endDate);
            var eventsList = GetPQEventsDetail(deviceNames, startDate, endDate);

            var dashboardData = new
            {
                orgName = StripModelPrefix(selectedPath),
                deviceCount = deviceNames.Count,
                timeRange = GetTimeRangeDescription(daysInRange),
                lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                summary = summaryData,
                events = eventsList,
                eventFrequencyChart = GetEventFrequencyChartData(eventsList, startDate, endDate),
                eventDurationChart = GetEventDurationChartData(summaryData),
                eventTypesChart = GetEventTypesChartData(summaryData)
            };

            WritePQEventsJSON(dashboardData);
        }
        catch (Exception ex)
        {
            Log.Error("PQEvents_LogAggregator", "Error aggregating PQ events: " + ex.Message);
            WriteEmptyPQEventsDashboard();
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

    private List<object> GetPQEventsSummary(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var summary = new List<object>();
        
        try
        {
            if (commonStore == null || deviceNames.Count == 0)
                return summary;

            string escapedNames = string.Join("','", deviceNames.Select(n => (n ?? "").Replace("'", "''")));
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");

            string query = "SELECT Event_Type, Sub_Event, COUNT(*) as Count, " +
                          "MAX(Local_Timestamp) as LatestTime, AVG(Event_Duration_mS) as AvgDuration " +
                          "FROM " + PQ_LOG_TABLE + " " +
                          "WHERE DeviceName IN ('" + escapedNames + "') " +
                          "AND Local_Timestamp >= '" + startDateStr + "' " +
                          "AND Local_Timestamp < '" + endDateStr + "' " +
                          "GROUP BY Event_Type, Sub_Event " +
                          "ORDER BY LatestTime DESC, Count DESC";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results != null && results.GetLength(0) > 0)
            {
                for (int i = 0; i < results.GetLength(0); i++)
                {
                    summary.Add(new
                    {
                        eventType = results[i, 0]?.ToString() ?? "",
                        subEvent = results[i, 1]?.ToString() ?? "",
                        count = Convert.ToInt32(results[i, 2] ?? 0),
                        latestTime = results[i, 3]?.ToString() ?? "",
                        avgDuration = results[i, 4] != null ? Math.Round(Convert.ToDouble(results[i, 4]), 2) : 0
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error getting PQ summary: " + ex.Message);
        }

        return summary;
    }

    private List<object> GetPQEventsDetail(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var events = new List<object>();
        
        try
        {
            if (commonStore == null || deviceNames.Count == 0)
                return events;

            string escapedNames = string.Join("','", deviceNames.Select(n => (n ?? "").Replace("'", "''")));
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");

            string query = "SELECT Record_Identifier, DeviceName, Event_Type, Sub_Event, " +
                          "Local_Timestamp, Event_Duration_mS, Trip_Point, Min_or_Max, Association_Timestamp " +
                          "FROM " + PQ_LOG_TABLE + " " +
                          "WHERE DeviceName IN ('" + escapedNames + "') " +
                          "AND Local_Timestamp >= '" + startDateStr + "' " +
                          "AND Local_Timestamp < '" + endDateStr + "' " +
                          "ORDER BY Local_Timestamp DESC";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results != null && results.GetLength(0) > 0)
            {
                for (int i = 0; i < results.GetLength(0); i++)
                {
                    try
                    {
                        double duration = SafeParseDouble(results[i, 5], 2);
                        double minOrMax = SafeParseDouble(results[i, 7], 3);

                        bool hasWaveform = results[i, 8] != null && 
                                          !string.IsNullOrEmpty(results[i, 8].ToString()) && 
                                          results[i, 8].ToString() != "0-00-00 00:00:00.000000";

                        events.Add(new
                        {
                            recordId = results[i, 0]?.ToString() ?? "",
                            deviceName = results[i, 1]?.ToString() ?? "",
                            eventType = results[i, 2]?.ToString() ?? "",
                            subEvent = results[i, 3]?.ToString() ?? "",
                            timestamp = results[i, 4]?.ToString() ?? "",
                            duration = duration,
                            tripPoint = results[i, 6]?.ToString() ?? "",
                            minOrMax = minOrMax,
                            hasWaveform = hasWaveform
                        });
                    }
                    catch (Exception rowEx)
                    {
                        Log.Warning("PQEvents_LogAggregator", $"Error processing row {i}: {rowEx.Message}");
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error getting PQ events detail: " + ex.Message);
        }

        return events;
    }

    private object GetEventFrequencyChartData(List<object> events, DateTime startDate, DateTime endDate)
    {
        var timestamps = new List<string>();
        var counts = new List<int>();
        var eventsByDate = new Dictionary<string, int>();

        try
        {
            DateTime current = startDate.Date;
            while (current <= endDate.Date)
            {
                string dateStr = current.ToString("yyyy-MM-dd");
                eventsByDate[dateStr] = 0;
                current = current.AddDays(1);
            }

            foreach (dynamic eventObj in events)
            {
                try
                {
                    string tsStr = eventObj.timestamp;
                    if (DateTime.TryParse(tsStr, out DateTime ts))
                    {
                        string dateStr = ts.ToString("yyyy-MM-dd");
                        if (eventsByDate.ContainsKey(dateStr))
                            eventsByDate[dateStr]++;
                    }
                }
                catch { }
            }

            foreach (var kvp in eventsByDate.OrderBy(x => x.Key))
            {
                timestamps.Add(kvp.Key);
                counts.Add(kvp.Value);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error building frequency chart: " + ex.Message);
        }

        return new { timestamps, counts };
    }

    private object GetEventDurationChartData(List<object> summaryData)
    {
        var eventTypes = new List<string>();
        var durations = new List<double>();

        try
        {
            foreach (dynamic item in summaryData)
            {
                try
                {
                    // Include subEvent to make each entry unique and meaningful
                    string displayName = item.subEvent?.ToString() ?? item.eventType?.ToString() ?? "Unknown";
                    eventTypes.Add(displayName);
                    durations.Add(item.avgDuration);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error building duration chart: " + ex.Message);
        }

        return new { eventTypes, durations };
    }

    private object GetEventTypesChartData(List<object> summaryData)
    {
        var data = new List<object>();
        var aggregatedCounts = new Dictionary<string, int>();

        try
        {
            // Aggregate counts by event type
            foreach (dynamic item in summaryData)
            {
                try
                {
                    string eventType = item.eventType;
                    int count = item.count;
                    
                    if (aggregatedCounts.ContainsKey(eventType))
                    {
                        aggregatedCounts[eventType] += count;
                    }
                    else
                    {
                        aggregatedCounts[eventType] = count;
                    }
                }
                catch { }
            }

            // Create data array with aggregated values
            foreach (var kvp in aggregatedCounts.OrderByDescending(x => x.Value))
            {
                data.Add(new
                {
                    name = kvp.Key,
                    value = kvp.Value
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error building event types chart: " + ex.Message);
        }

        return new { data };
    }

    private string GetTimeRangeDescription(int days)
    {
        if (days == 1) return "Last 24 Hours";
        if (days == 7) return "Last 7 Days";
        if (days == 30) return "Last 30 Days";
        return $"Last {days} Days";
    }

    private double SafeParseDouble(object value, int roundDigits = 2)
    {
        if (value == null)
            return 0;

        try
        {
            string strValue = value.ToString().Trim();
            // Remove percentage symbols and other non-numeric characters except decimal point and negative sign
            strValue = strValue.Replace("%", "").Trim();
            
            if (double.TryParse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedValue))
                return Math.Round(parsedValue, roundDigits);
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", $"Error parsing value '{value}': {ex.Message}");
        }

        return 0;
    }

    private List<string> GetDeviceNamesFromOrgNode(string orgNodePath)
    {
        var deviceNames = new List<string>();

        try
        {
            if (string.IsNullOrEmpty(orgNodePath))
            {
                var modelFolder = Project.Current.Get("Model");
                if (modelFolder != null)
                {
                    CollectDeviceNames(modelFolder, deviceNames);
                }
            }
            else
            {
                var orgNode = Project.Current.Get(orgNodePath);
                if (orgNode != null)
                {
                    CollectDeviceNames(orgNode, deviceNames);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("PQEvents_LogAggregator", "Error getting device names: " + ex.Message);
        }

        return deviceNames;
    }

    private void CollectDeviceNames(IUANode node, List<string> deviceNames)
    {
        try
        {
            var deviceAlarms = node.GetObject("DeviceAlarms");
            var deviceStatus = node.GetObject("DeviceStatus");

            if (deviceAlarms != null && deviceStatus != null)
            {
                deviceNames.Add(node.BrowseName);
            }

            foreach (var child in node.Children)
            {
                CollectDeviceNames(child, deviceNames);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error in CollectDeviceNames: " + ex.Message);
        }
    }

    private void WritePQEventsJSON(object dashboardData)
    {
        try
        {
            var projectPath = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDir = projectPath.Uri;
            string resDir = Path.Combine(baseDir, "res", "TabCommon_PQEventsDashboard");

            if (!Directory.Exists(resDir))
                Directory.CreateDirectory(resDir);

            string filePath = Path.Combine(resDir, "pq-events-dashboard-data.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dashboardData, options);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Log.Error("PQEvents_LogAggregator", "Error writing JSON: " + ex.Message);
        }
    }

    private void WriteEmptyPQEventsDashboard()
    {
        string selectedPath = selectedOrgNodeVar?.Value ?? "";
        var emptyData = new
        {
            
            orgName = StripModelPrefix(selectedPath),
            deviceCount = 0,
            timeRange = "No Data",
            lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            summary = new List<object>(),
            events = new List<object>(),
            eventFrequencyChart = new { timestamps = new List<string>(), counts = new List<int>() },
            eventDurationChart = new { eventTypes = new List<string>(), durations = new List<double>() },
            eventTypesChart = new { data = new List<object>() }
        };

        WritePQEventsJSON(emptyData);
    }

    private void RefreshPQEventsDashboardBrowser()
    {
        try
        {
            pqEventsDashboardBrowser = Owner.Get<WebBrowser>("wb_PQEventsDashboard");
            if (pqEventsDashboardBrowser != null)
            {                
                pqEventsDashboardBrowser.Refresh();    
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error refreshing browser: " + ex.Message);
        }
    }

    private void SubscribeToNewEventTriggers(string orgNodePath)
    {
        try
        {
            if (string.IsNullOrEmpty(orgNodePath))
                return;

            var deviceNames = GetDeviceNamesFromOrgNode(orgNodePath);
            var orgNode = Project.Current.Get(orgNodePath);
            
            if (orgNode == null)
            {
                Log.Warning("PQEvents_LogAggregator", "Could not get org node: " + orgNodePath);
                return;
            }

            foreach (var deviceName in deviceNames)
            {
                try
                {
                    // Search through org node's children to find the device
                    IUANode deviceNode = null;
                    foreach (var child in orgNode.Children)
                    {
                        if (child.BrowseName == deviceName)
                        {
                            deviceNode = child;
                            break;
                        }
                    }

                    if (deviceNode != null)
                    {
                        // Subscribe to NewEventTrigger variable
                        var newEventTrigger = deviceNode.GetVariable("db_PQLogger/NewEventTrigger");
                        if (newEventTrigger != null)
                        {
                            newEventTrigger.VariableChange += OnNewPQEventTriggered;
                            newEventTriggerSubscriptions[deviceName] = newEventTrigger;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("PQEvents_LogAggregator", $"Error subscribing to device {deviceName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error subscribing to event triggers: " + ex.Message);
        }
    }

    private void UnsubscribeFromNewEventTriggers()
    {
        try
        {
            foreach (var kvp in newEventTriggerSubscriptions)
            {
                try
                {
                    kvp.Value.VariableChange -= OnNewPQEventTriggered;
                }
                catch (Exception ex)
                {
                    Log.Warning("PQEvents_LogAggregator", $"Error unsubscribing from {kvp.Key}: {ex.Message}");
                }
            }

            newEventTriggerSubscriptions.Clear();
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error clearing subscriptions: " + ex.Message);
        }
    }

    private void OnNewPQEventTriggered(object sender, VariableChangeEventArgs e)
    {
        try
        {
            var triggerVar = (IUAVariable)sender;
            bool isTriggered = (bool)(e.NewValue ?? false);

            if (isTriggered)
            {
                // Refresh with current time range
                LoadPQEventsForDays(timeRangeDays);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PQEvents_LogAggregator", "Error handling new event trigger: " + ex.Message);
        }
    }
}
