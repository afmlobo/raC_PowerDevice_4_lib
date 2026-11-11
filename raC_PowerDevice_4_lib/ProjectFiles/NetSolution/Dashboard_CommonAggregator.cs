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
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;


public class Dashboard_CommonAggregator : BaseNetLogic
{
    private PeriodicTask aggregationTask;
    private IUAVariable selectedOrgNodeVar;
    private Store commonStore;

    // Table names
    private const string ENERGY_LOG_TABLE = "EnergyLog_AllDevices";
    private const string ALARM_LOG_TABLE = "AlarmLog_AllDevices";
    private const string PQ_LOG_TABLE = "PowerQualityLog_AllDevices";

    // Column name mappings for EnergyLog_AllDevices
    private const string COL_DEVICE_NAME = "DeviceName";
    private const string COL_TIMESTAMP = "Timestamp";
    private const string COL_KWH_FWD = "kWh_Fwd";  // Energy consumed (forward)
    private const string COL_KW_DEMAND = "kW_Demand";  // Power demand

    public override void Start()
    {
        // Get reference to SelectedOrgNodePath variable from parent NavigationPanelright
        try
        {
            var navigationPanel = Owner.Owner.Owner; // Navigate up to NavigationPanelright
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            
            if (selectedOrgNodeVar == null)
            {
                Log.Warning("DashboardAggregator", "SelectedOrgNodePath variable not found in NavigationPanelright, creating local copy");
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
            Log.Warning("DashboardAggregator", $"Error getting SelectedOrgNodePath: {ex.Message}");
        }

        // Get common database store reference
        try
        {
            commonStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (commonStore == null)
            {
                Log.Error("DashboardAggregator", "Common store not found!");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("DashboardAggregator", $"Failed to get common store: {ex.Message}");
            return;
        }

        aggregationTask = new PeriodicTask(task => AggregateDashboardData(task), 30000, LogicObject);
        aggregationTask.Start();
    }

    public override void Stop()
    {
        if (selectedOrgNodeVar != null)
        {
            selectedOrgNodeVar.VariableChange -= OnSelectedOrgNodeChanged;
        }

        aggregationTask?.Dispose();
    }

    private void OnSelectedOrgNodeChanged(object sender, UAManagedCore.VariableChangeEventArgs args)
    {
        AggregateDashboardData(null, showLoading: true);
    }

    private void AggregateDashboardData(PeriodicTask task, bool showLoading = false)
    {
        try
        {
            string selectedOrgPath = selectedOrgNodeVar?.Value ?? "";

            // Skip if org path is not properly initialized (e.g., "0" or other invalid default)
            if (!string.IsNullOrEmpty(selectedOrgPath) && (selectedOrgPath == "0" || !selectedOrgPath.Contains("/")))
            {
                return;
            }

            if (showLoading)
            {
                var loadingState = new
                {
                    isLoading = true,
                    timestamp = DateTime.Now.ToString("o"),
                    orgNodePath = StripModelPrefix(selectedOrgPath),
                    orgNodeName = GetOrgNodeName(selectedOrgPath),
                    loadingStarted = DateTime.Now.ToString("HH:mm:ss")
                };
                WriteDashboardJSON(loadingState);
            }

            // Collect all data
            var dashboardData = new
            {
                isLoading = false,
                timestamp = DateTime.Now.ToString("o"),
                orgNodePath = StripModelPrefix(selectedOrgPath),
                orgNodeName = GetOrgNodeName(selectedOrgPath),

                // Main KPIs
                activeDevices = GetActiveDevicesData(selectedOrgPath),
                energyToday = GetEnergyTodayData(selectedOrgPath),
                activeAlarms = GetActiveAlarmsData(selectedOrgPath),
                powerQuality = GetPowerQualityData(selectedOrgPath),

                // Charts
                energyTrend = GetEnergyTrendData(selectedOrgPath),

                // Detail panels
                alarmDetails = GetAlarmDetailsData(selectedOrgPath),
                pqEventDetails = GetPQEventDetailsData(selectedOrgPath),

                // Additional metadata
                lastUpdated = DateTime.Now.ToString("HH:mm:ss")
            };

            // Write to JSON file with loading complete flag
            WriteDashboardJSON(dashboardData);
        }
        catch (Exception ex)
        {
            Log.Error("DashboardAggregator", $"Error aggregating dashboard data: {ex.Message}");
        }
    }

    #region Active Devices

    private object GetActiveDevicesData(string orgPath)
    {
        var devices = GetDevicesInOrgNode(orgPath);
        int activeCount = 0;
        int offlineCount = 0;

        foreach (var device in devices)
        {
            var statusNode = device.GetObject("DeviceStatus");
            if (statusNode == null) continue;

            var statusVar = statusNode.GetVariable("Status");
            if (statusVar != null && Convert.ToString(statusVar.Value.Value) == "Ready")
            {
                activeCount++;
            }
            else
            {
                offlineCount++;
            }
        }

        int totalCount = activeCount + offlineCount;
        double changePercent = CalculateDeviceChangePercent(activeCount, totalCount);

        return new
        {
            value = activeCount,
            total = totalCount,
            offline = offlineCount,
            change = $"{(changePercent >= 0 ? "+" : "")}{changePercent:F1}%",
            trend = changePercent >= 0 ? "up" : "down",
            subtext = $"{offlineCount} Offline, {totalCount} Total"
        };
    }

    private double CalculateDeviceChangePercent(int current, int total)
    {
        return total > 0 ? ((double)current / total) * 100 : 0;
    }

    #endregion

    #region Energy Today

 private object GetEnergyTodayData(string orgPath)
{
    try
    {
        if (commonStore == null)
            return CreateEmptyEnergyData();

        var devices = GetDevicesInOrgNode(orgPath);
        if (devices.Count == 0)
            return CreateEmptyEnergyData();

        var deviceNames = devices.Select(d => d.BrowseName).ToList();
        var deviceFilter = string.Join(",", deviceNames.Select(n => "'" + n + "'"));

        DateTime today = DateTime.Today;
        DateTime now = DateTime.Now;
        DateTime yesterday = today.AddDays(-1);
        DateTime tomorrow = today.AddDays(1);

        string todayStartStr = today.ToString("yyyy-MM-ddTHH:mm:ss");
        string tomorrowStartStr = tomorrow.ToString("yyyy-MM-ddTHH:mm:ss");
        string yesterdayStartStr = yesterday.ToString("yyyy-MM-ddTHH:mm:ss");

        // Query for today's energy consumption - using simple timestamp comparison
        string query =
            "SELECT DeviceName, MIN(kWh_Fwd) AS MinReading, MAX(kWh_Fwd) AS MaxReading " +
            "FROM EnergyLog_AllDevices " +
            "WHERE DeviceName IN (" + deviceFilter + ") " +
            "AND Timestamp >= '" + todayStartStr + "' " +
            "AND Timestamp < '" + tomorrowStartStr + "' " +
            "GROUP BY DeviceName";

        object[,] result;
        string[] columns;
        
        commonStore.Query(query, out columns, out result);

        double totalEnergy = 0;
        if (result != null && result.GetLength(0) > 0)
        {
            for (int i = 0; i < result.GetLength(0); i++)
            {
                // result[i, 1] = MinReading, result[i, 2] = MaxReading
                double minVal = result[i, 1] != null ? Convert.ToDouble(result[i, 1]) : 0;
                double maxVal = result[i, 2] != null ? Convert.ToDouble(result[i, 2]) : 0;
                totalEnergy += (maxVal - minVal);
            }
        }

        // Yesterday query - using simple timestamp comparison
        string yesterdayQuery = "SELECT DeviceName, MIN(kWh_Fwd) AS MinReading, MAX(kWh_Fwd) AS MaxReading " +
                                "FROM EnergyLog_AllDevices " +
                                "WHERE DeviceName IN (" + deviceFilter + ") " +
                                "AND Timestamp >= '" + yesterdayStartStr + "' " +
                                "AND Timestamp < '" + todayStartStr + "' " +
                                "GROUP BY DeviceName";

        commonStore.Query(yesterdayQuery, out columns, out result);

        double yesterdayEnergy = 0;
        if (result != null && result.GetLength(0) > 0)
        {
            for (int i = 0; i < result.GetLength(0); i++)
            {
                // result[i, 1] = MinReading, result[i, 2] = MaxReading
                double minVal = result[i, 1] != null ? Convert.ToDouble(result[i, 1]) : 0;
                double maxVal = result[i, 2] != null ? Convert.ToDouble(result[i, 2]) : 0;
                yesterdayEnergy += (maxVal - minVal);
            }
        }

        double changePercent = yesterdayEnergy > 0
            ? (totalEnergy - yesterdayEnergy) / yesterdayEnergy * 100.0
            : 0;

        string trend = changePercent >= 0 ? "up" : "down";
        string changeText = changePercent >= 0
            ? "+" + changePercent.ToString("F1") + "%"
            : changePercent.ToString("F1") + "%";

        return new
        {
            value = Math.Round(totalEnergy, 2),
            unit = "kWh",
            change = changeText,
            trend = trend,
            subtext = "kWh consumed today"
        };
    }
    catch (Exception ex)
    {
        Log.Error("DashboardAggregator", "Error calculating energy today: " + ex.Message + "\n" + ex.StackTrace);
        return CreateEmptyEnergyData();
    }
}


    private object CreateEmptyEnergyData()
    {
        return new
        {
            value = 0,
            unit = "kWh",
            change = "0%",
            trend = "neutral",
            subtext = "No data available"
        };
    }

    #endregion

    #region Active Alarms

    private object GetActiveAlarmsData(string orgPath)
    {
        int criticalCount = 0;
        int warningCount = 0;
        int infoCount = 0;

        var devices = GetDevicesInOrgNode(orgPath);

        foreach (var device in devices)
        {
            var deviceAlarmsNode = device.GetObject("DeviceAlarms");
            if (deviceAlarmsNode == null) continue;

            var overallStatus = deviceAlarmsNode.GetVariable("OverallAlarmStatus")?.Value;
            if (overallStatus == null || !(bool)overallStatus) continue;

            var alarms = CategorizeDeviceAlarms(deviceAlarmsNode);
            criticalCount += alarms.Critical;
            warningCount += alarms.Warning;
            infoCount += alarms.Info;
        }

        int totalAlarms = criticalCount + warningCount + infoCount;

        return new
        {
            value = totalAlarms,
            critical = criticalCount,
            warning = warningCount,
            info = infoCount,
            subtext = $"{criticalCount} Critical, {warningCount} Warning, {infoCount} Info"
        };
    }

    private AlarmCategorization CategorizeDeviceAlarms(IUANode deviceAlarmsNode)
    {
        var result = new AlarmCategorization();

        // Critical alarms - Voltage issues, metering, overall harmonics
        var criticalAlarms = new[]
        {
            "IEEE1159_Over_Voltage",
            "IEEE1159_Under_Voltage",
            "IEEE1159_DCOffset_Condition",
            "Metering_Status",
            "IEEE519_Overall_Status"
        };

        foreach (var alarmName in criticalAlarms)
        {
            var alarmVar = deviceAlarmsNode.GetVariable(alarmName);
            if (alarmVar != null && (int)alarmVar.Value != 0)
            {
                result.Critical++;
            }
        }

        // Warning alarms - THD, frequency, imbalance, harmonics, fluctuation, deviations
        var warningAlarms = new[]
        {
            "IEEE1159_Voltage_THD_Condition",
            "IEEE1159_Current_THD_Condition",
            "IEEE1159_PowerFrequency_Condition",
            "IEEE1159_Imbalance_Condition",
            "PowerQuality_Status",
            "IEEE1159_Voltage_Fluctuation_Condition",
            "ShortTerm_2nd_To_17th_Harmonic_Status",
            "ShortTerm_18th_To_33rd_Harmonic_Status",
            "ShortTerm_34th_To_40th_Harmonic_Status",
            "LongTerm_2nd_To_17th_Harmonic_Status",
            "LongTerm_18th_To_33rd_Harmonic_Status",
            "LongTerm_34th_To_40th_Harmonic_Status",
            "EN61000_4_30_Mains_Signaling_Condition",
            "EN61000_4_30_Under_Deviation_Condition",
            "EN61000_4_30_Over_Deviation_Condition"
        };

        foreach (var alarmName in warningAlarms)
        {
            var alarmVar = deviceAlarmsNode.GetVariable(alarmName);
            if (alarmVar != null && (int)alarmVar.Value != 0)
            {
                result.Warning++;
            }
        }

        // Info alarms - Setpoints, logic, logs, overrun
        var infoAlarms = new[]
        {
            "Setpoints_1_10_Active",
            "Setpoints_11_20_Active",
            "Logic_Level_1_Gates_Active",
            "Logs_Status",
            "Over_Range_Information",
            "Output_Pulse_Overrun"
        };

        foreach (var alarmName in infoAlarms)
        {
            var alarmVar = deviceAlarmsNode.GetVariable(alarmName);
            if (alarmVar != null && (int)alarmVar.Value != 0)
            {
                result.Info++;
            }
        }

        return result;
    }

    #endregion

    #region Power Quality

    private object GetPowerQualityData(string orgPath)
    {
        try
        {
            if (commonStore == null)
            {
                return CreateEmptyPowerQualityData();
            }

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
            {
                return CreateEmptyPowerQualityData();
            }

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            string deviceFilter = string.Join("','", deviceNames);

            // Count PQ events today
            DateTime today = DateTime.Today;
            DateTime now = DateTime.Now;
            DateTime yesterday = today.AddDays(-1);

            var query = $@"
                SELECT COUNT(*) as EventCount 
                FROM {PQ_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Local_Timestamp >= '{today:yyyy-MM-ddTHH:mm:ss}' 
                    AND Local_Timestamp <= '{now:yyyy-MM-ddTHH:mm:ss}'";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            int todayEvents = 0;
            if (result != null && result.GetLength(0) > 0 && result[0, 0] != null)
            {
                todayEvents = Convert.ToInt32(result[0, 0]);
            }

            // Count PQ events yesterday
            var yesterdayQuery = $@"
                SELECT COUNT(*) as EventCount 
                FROM {PQ_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Local_Timestamp >= '{yesterday:yyyy-MM-ddTHH:mm:ss}' 
                    AND Local_Timestamp < '{today:yyyy-MM-ddTHH:mm:ss}'";

            commonStore.Query(yesterdayQuery, out columns, out result);

            int yesterdayEvents = 0;
            if (result != null && result.GetLength(0) > 0 && result[0, 0] != null)
            {
                yesterdayEvents = Convert.ToInt32(result[0, 0]);
            }

            // Calculate percentage change from yesterday
            double percentChange = 0;
            if (yesterdayEvents > 0)
            {
                percentChange = ((double)(todayEvents - yesterdayEvents) / yesterdayEvents) * 100;
            }
            else if (todayEvents > 0)
            {
                percentChange = 100; // If yesterday had 0 but today has events, that's 100% increase
            }

            string trend = percentChange >= 0 ? "up" : "down";
            string changeText = percentChange >= 0
                ? "+" + percentChange.ToString("F1") + "%"
                : percentChange.ToString("F1") + "%";

            // Calculate power quality percentage for status
            int minutesToday = (int)(now - today).TotalMinutes;
            double qualityPercent = minutesToday > 0 ? Math.Max(0, ((minutesToday - todayEvents) / (double)minutesToday) * 100) : 100;
            string qualityStatus = qualityPercent >= 95 ? "Good" : qualityPercent >= 85 ? "Fair" : "Poor";

            return new
            {
                value = todayEvents,
                unit = "Events",
                status = qualityStatus,
                change = changeText,
                trend = trend,
                subtext = $"{yesterdayEvents} events yesterday"
            };
        }
        catch (Exception ex)
        {
            Log.Error("DashboardAggregator", $"Error calculating power quality: {ex.Message}");
            return CreateEmptyPowerQualityData();
        }
    }

    private object CreateEmptyPowerQualityData()
    {
        return new
        {
            value = 0,
            unit = "Events",
            status = "Unknown",
            change = "0%",
            trend = "neutral",
            subtext = "No data available"
        };
    }

    #endregion

    #region Energy Trend

    private object GetEnergyTrendData(string orgPath)
    {
        try
        {
            if (commonStore == null)
            {
                return new
                {
                    defaultRange = "24hours",
                    periodSize = "hour",
                    availableRanges = new[] { "live", "24hours", "7days", "30days" },
                    data = new List<object>()
                };
            }

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
            {
                return new
                {
                    defaultRange = "24hours",
                    periodSize = "hour",
                    availableRanges = new[] { "live", "24hours", "7days", "30days" },
                    data = new List<object>()
                };
            }

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            string deviceFilter = string.Join("','", deviceNames);

            // Align to hour boundaries for consistent slot generation
            DateTime now = DateTime.Now;
            DateTime endTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            DateTime startTime = endTime.AddDays(-30);

            // Calculate energy consumption per period - Get MIN/MAX separately, calculate in C#
            var query = $@"
                SELECT 
                    {COL_DEVICE_NAME},
                    {COL_TIMESTAMP},
                    {COL_KWH_FWD}
                FROM {ENERGY_LOG_TABLE} 
                WHERE {COL_DEVICE_NAME} IN ('{deviceFilter}') 
                    AND {COL_TIMESTAMP} >= '{startTime:yyyy-MM-ddTHH:mm:ss}'
                    AND {COL_TIMESTAMP} <= '{endTime:yyyy-MM-ddTHH:mm:ss}'
                ORDER BY {COL_TIMESTAMP}";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            var trendData = new List<object>();
            var periodGroups = new Dictionary<string, List<double>>(); // Group readings by period

            if (result != null)
            {
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    // result[i, 1] = Timestamp, result[i, 2] = kWh_Fwd
                    if (result[i, 1] == null || result[i, 2] == null)
                        continue;

                    DateTime timestamp = Convert.ToDateTime(result[i, 1]);
                    double reading = Convert.ToDouble(result[i, 2]);

                    // Group by hour for consistent client-side slicing
                    string period = timestamp.ToString("yyyy-MM-dd HH:00:00");

                    // Store readings grouped by period
                    if (!periodGroups.ContainsKey(period))
                    {
                        periodGroups[period] = new List<double>();
                    }
                    periodGroups[period].Add(reading);
                }
            }

            // Pre-generate all hourly slots for 30-day period to ensure consistent data representation
            // This prevents gaps in data from appearing as shifted timelines
            var allHourlySlots = new Dictionary<string, double>();
            int totalHours = (int)((endTime - startTime).TotalHours) + 1;
            
            for (int hourOffset = 0; hourOffset < totalHours; hourOffset++)
            {
                DateTime slotTime = startTime.AddHours(hourOffset);
                string slotKey = slotTime.ToString("yyyy-MM-dd HH:00:00");
                
                if (periodGroups.ContainsKey(slotKey))
                {
                    // Data exists for this hour
                    double minReading = periodGroups[slotKey].Min();
                    double maxReading = periodGroups[slotKey].Max();
                    double energy = Math.Max(0, maxReading - minReading);
                    allHourlySlots[slotKey] = Math.Round(energy, 2);
                }
                else
                {
                    // No data for this hour - mark as 0 (or missing)
                    allHourlySlots[slotKey] = 0;
                }
            }

            // Convert to list and return
            foreach (var kvp in allHourlySlots.OrderBy(x => x.Key))
            {
                trendData.Add(new { period = kvp.Key, energy = kvp.Value });
            }

            return new
            {
                defaultRange = "24hours",
                periodSize = "hour",
                availableRanges = new[] { "live", "24hours", "7days", "30days" },
                data = trendData
            };
        }
        catch (Exception ex)
        {
            Log.Error("DashboardAggregator", $"Error getting energy trend: {ex.Message}");
            return new
            {
                defaultRange = "24hours",
                periodSize = "hour",
                availableRanges = new[] { "live", "24hours", "7days", "30days" },
                data = new List<object>()
            };
        }
    }

    #endregion

    #region Alarm Details

    private object GetAlarmDetailsData(string orgPath)
    {
        var alarmDetails = new List<object>();
        var devices = GetDevicesInOrgNode(orgPath);

        foreach (var device in devices)
        {
            var deviceAlarmsNode = device.GetObject("DeviceAlarms");
            if (deviceAlarmsNode == null) continue;

            var overallStatus = deviceAlarmsNode.GetVariable("OverallAlarmStatus")?.Value;
            if (overallStatus == null || !(bool)overallStatus) continue;

            var deviceName = device.BrowseName;
            var timestamp = DateTime.Now;

            // Check all alarm types
            var allAlarms = new Dictionary<string, string>
            {
                // Critical alarms
                { "IEEE1159_Over_Voltage", "Critical" },
                { "IEEE1159_Under_Voltage", "Critical" },
                { "IEEE1159_DCOffset_Condition", "Critical" },
                { "Metering_Status", "Critical" },
                { "IEEE519_Overall_Status", "Critical" },
                
                // Warning alarms
                { "IEEE1159_Voltage_THD_Condition", "Warning" },
                { "IEEE1159_Current_THD_Condition", "Warning" },
                { "IEEE1159_PowerFrequency_Condition", "Warning" },
                { "IEEE1159_Imbalance_Condition", "Warning" },
                { "PowerQuality_Status", "Warning" },
                { "IEEE1159_Voltage_Fluctuation_Condition", "Warning" },
                { "ShortTerm_2nd_To_17th_Harmonic_Status", "Warning" },
                { "ShortTerm_18th_To_33rd_Harmonic_Status", "Warning" },
                { "ShortTerm_34th_To_40th_Harmonic_Status", "Warning" },
                { "LongTerm_2nd_To_17th_Harmonic_Status", "Warning" },
                { "LongTerm_18th_To_33rd_Harmonic_Status", "Warning" },
                { "LongTerm_34th_To_40th_Harmonic_Status", "Warning" },
                { "EN61000_4_30_Mains_Signaling_Condition", "Warning" },
                { "EN61000_4_30_Under_Deviation_Condition", "Warning" },
                { "EN61000_4_30_Over_Deviation_Condition", "Warning" },
                
                // Info alarms
                { "Setpoints_1_10_Active", "Info" },
                { "Setpoints_11_20_Active", "Info" },
                { "Logic_Level_1_Gates_Active", "Info" },
                { "Logs_Status", "Info" },
                { "Over_Range_Information", "Info" },
                { "Output_Pulse_Overrun", "Info" }
            };

            foreach (var alarm in allAlarms)
            {
                var alarmVar = deviceAlarmsNode.GetVariable(alarm.Key);
                if (alarmVar != null && (int)alarmVar.Value != 0)
                {
                    alarmDetails.Add(new
                    {
                        severity = alarm.Value,
                        message = alarm.Key,
                        device = deviceName,
                        timestamp = timestamp.ToString("HH:mm"),
                        acknowledge = "ACK"
                    });
                }
            }
        }

        // Return all alarms sorted by severity (Critical > Warning > Info)
        // This ensures Info alarms are not filtered out when there are many Critical/Warning alarms
        return alarmDetails
            .OrderBy(a => ((dynamic)a).severity == "Critical" ? 0 : ((dynamic)a).severity == "Warning" ? 1 : 2)
            .ToList();
    }

    #endregion

    #region PQ Event Details

    private object GetPQEventDetailsData(string orgPath)
    {
        try
        {
            if (commonStore == null)
            {
                return new List<object>();
            }

            var devices = GetDevicesInOrgNode(orgPath);
            if (devices.Count == 0)
            {
                return new List<object>();
            }

            var deviceNames = devices.Select(d => d.BrowseName).ToList();
            string deviceFilter = string.Join("','", deviceNames);

            // Get last 24 hours of events, grouped by type
            DateTime startTime = DateTime.Now.AddHours(-24);

            var query = $@"
                SELECT 
                    Event_Type,
                    COUNT(*) as EventCount,
                    MAX(Local_Timestamp) as LastOccurrence
                FROM {PQ_LOG_TABLE} 
                WHERE DeviceName IN ('{deviceFilter}') 
                    AND Local_Timestamp >= '{startTime:yyyy-MM-ddTHH:mm:ss}'
                GROUP BY Event_Type
                ORDER BY EventCount DESC
                LIMIT 5";

            object[,] result;
            string[] columns;
            commonStore.Query(query, out columns, out result);

            var pqEvents = new List<object>();

            if (result != null)
            {
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    pqEvents.Add(new
                    {
                        type = result[i, 0]?.ToString() ?? "Unknown",
                        count = result[i, 1] != null ? Convert.ToInt32(result[i, 1]) : 0,
                        lastOccurrence = result[i, 2]?.ToString() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
            }

            return pqEvents;
        }
        catch (Exception ex)
        {
            Log.Error("DashboardAggregator", $"Error getting PQ events: {ex.Message}");
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
                CollectDeviceNodes(modelFolder, devices);
            return devices;
        }

        var orgNode = Project.Current.Get(orgPath);
        if (orgNode == null)
            return devices;

        CollectDeviceNodes(orgNode, devices);
        return devices;
    }

    private void CollectDeviceNodes(IUANode node, List<IUANode> devices)
    {
        if (node.GetObject("DeviceAlarms") != null && node.GetObject("DeviceStatus") != null)
            devices.Add(node);

        foreach (var child in node.Children)
            CollectDeviceNodes(child, devices);
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

    private string StripModelPrefix(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return "";

        const string prefix = "Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel/";
        if (fullPath.StartsWith(prefix))
            return fullPath.Substring(prefix.Length);

        return fullPath;
    }

    private void WriteDashboardJSON(object data)
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res/TabCommon_CommonDashboard");
            string filePath = Path.Combine(directory, "Dashboard_Data.json");

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
            Log.Error("DashboardAggregator", $"Failed to save dashboard JSON: {ex.Message}");
        }
    }

    #endregion

    #region Exported Methods

    [ExportMethod]
    public void RefreshDashboard()
    {
        AggregateDashboardData(null, showLoading: true);
    }

    [ExportMethod]
    public void SetTimeRange(string timeRange)
    {
        AggregateDashboardData(null, showLoading: false);
    }

    #endregion
}

public class AlarmCategorization
{
    public int Critical { get; set; }
    public int Warning { get; set; }
    public int Info { get; set; }
}
