#region Using directives
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
#endregion

public class Dashboard_ITIAndSEMIF47ChartAggregator : BaseNetLogic
{
    private IUAVariable selectedOrgNodeVar;
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable selectedDaysVar;
    private IUAVariable errorMessageVar;
    private Store commonStore;

    private const string PQ_LOG_TABLE = "PowerQualityLog_AllDevices";
    
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-7);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 7;

    public override void Start()
    {
        try
        {
            var navigationPanel = Owner.Owner.Owner;
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            
            if (selectedOrgNodeVar == null)
            {
                Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", "SelectedOrgNodePath variable not found");
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
            Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", $"Error getting SelectedOrgNodePath: {ex.Message}");
        }

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
            
            if (selectedDaysVar != null)
                selectedDaysVar.Value = timeRangeDays;
            
            // Clear any error messages on start
            if (errorMessageVar != null)
                errorMessageVar.Value = "";
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", $"Error getting date variables: {ex.Message}");
        }

        try
        {
            commonStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (commonStore == null)
            {
                Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", "Common store not found!");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", $"Failed to get common store: {ex.Message}");
            return;
        }

        // Load initial data if org is already selected
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            if (!string.IsNullOrEmpty(selectedPath))
            {
                LoadITIDataForDays(timeRangeDays);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", "Error loading initial ITI data: " + ex.Message);
        }
    }

    public override void Stop()
    {
        if (selectedOrgNodeVar != null)
        {
            selectedOrgNodeVar.VariableChange -= OnSelectedOrgNodeChanged;
        }
    }

    private void OnSelectedOrgNodeChanged(object sender, VariableChangeEventArgs e)
    {
        string selectedPath = selectedOrgNodeVar?.Value ?? "";
        LoadITIDataForDays(timeRangeDays);
    }

    [ExportMethod]
    public void LoadITI24Hours() { LoadITIDataForDays(1); }

    [ExportMethod]
    public void LoadITI7Days() { LoadITIDataForDays(7); }

    [ExportMethod]
    public void LoadITI30Days() { LoadITIDataForDays(30); }

    [ExportMethod]
    public void LoadITIDataForDays(int days)
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
            
            // Clear any error messages
            if (errorMessageVar != null) errorMessageVar.Value = "";
            
            AggregateITIData(startDate, endDate, days);
            RefreshITIChartBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", "Error loading ITI data: " + ex.Message);
        }
    }

    [ExportMethod]
    public void LoadITICustomRange(DateTime startDate, DateTime endDate)
    {
        try
        {
            if (startDate >= endDate)
            {
                string errorMsg = "Error: Start date must be before end date";
                Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", errorMsg);
                
                // Display error message to user
                if (errorMessageVar != null)
                    errorMessageVar.Value = errorMsg;
                
                // Load blank data to clear dashboard
                WriteEmptyITIData();
                RefreshITIChartBrowser();
                return;
            }
            
            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;
            
            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = -1;
            
            // Clear any error messages
            if (errorMessageVar != null) errorMessageVar.Value = "";
            
            int days = (int)Math.Round((endDate - startDate).TotalDays);
            AggregateITIData(startDate, endDate, days);
            RefreshITIChartBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", "Error loading custom range: " + ex.Message);
        }
    }

    private void AggregateITIData(DateTime startDate, DateTime endDate, int daysInRange)
    {
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            var deviceNames = GetDeviceNamesFromOrgNode(selectedPath);
            
            if (deviceNames.Count == 0)
            {
                WriteEmptyITIData();
                return;
            }

            var itiPointsList = GetITIDataPoints(deviceNames, startDate, endDate);

            var dashboardData = new
            {
                orgName = StripModelPrefix(selectedPath),
                deviceCount = deviceNames.Count,
                timeRange = GetTimeRangeDescription(daysInRange),
                lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                points = itiPointsList,
                LastRefreshTimestamp = DateTime.Now.ToString("o")
            };

            WriteITIDataJSON(dashboardData);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", "Error aggregating ITI data: " + ex.Message);
            WriteEmptyITIData();
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

    private List<object> GetITIDataPoints(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var itiPoints = new List<object>();
        
        try
        {
            if (commonStore == null || deviceNames.Count == 0)
                return itiPoints;

            string escapedNames = string.Join("','", deviceNames.Select(n => (n ?? "").Replace("'", "''")));
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");

            string query = "SELECT Record_Identifier, DeviceName, Event_Type, Sub_Event, " +
                          "Local_Timestamp, Event_Duration_mS, Min_or_Max, Trip_Point " +
                          "FROM " + PQ_LOG_TABLE + " " +
                          "WHERE DeviceName IN ('" + escapedNames + "') " +
                          "AND Local_Timestamp >= '" + startDateStr + "' " +
                          "AND Local_Timestamp < '" + endDateStr + "' " +
                          "AND Event_Type IN ('IEEE1159_Swell', 'IEEE1159_Sag', 'Voltage_Swell', 'Voltage_Sag') " +
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
                        double cycles = 0;
                        if (results[i, 5] != null)
                        {
                            if (double.TryParse(results[i, 5].ToString(), out double durationMs))
                            {
                                double seconds = durationMs / 1000.0;
                                cycles = seconds * 60; // 60 Hz
                            }
                        }

                        double percentage = 0;
                        if (results[i, 6] != null)
                        {
                            string minOrMaxStr = results[i, 6].ToString().Replace("%", "").Trim();
                            if (double.TryParse(minOrMaxStr, out double minOrMax))
                            {
                                double nominalVoltage = 480;
                                double absoluteDifference = Math.Abs(minOrMax - nominalVoltage);
                                percentage = (absoluteDifference / nominalVoltage) * 100;
                            }
                        }

                        if (cycles > 0)
                        {
                            // Format timestamp as ISO 8601 for JavaScript compatibility
                            string timestampISO = "";
                            if (results[i, 4] != null)
                            {
                                if (DateTime.TryParse(results[i, 4].ToString(), out DateTime dt))
                                {
                                    timestampISO = dt.ToString("yyyy-MM-ddTHH:mm:ss");
                                }
                                else
                                {
                                    timestampISO = results[i, 4].ToString();
                                }
                            }

                            itiPoints.Add(new
                            {
                                recordId = results[i, 0]?.ToString() ?? "",
                                deviceName = results[i, 1]?.ToString() ?? "",
                                eventType = results[i, 2]?.ToString() ?? "",
                                subEvent = results[i, 3]?.ToString() ?? "",
                                timestamp = timestampISO,
                                cycles = Math.Round(cycles, 2),
                                percentage = Math.Round(percentage, 2),
                                tripPoint = results[i, 7]?.ToString() ?? ""
                            });
                        }
                    }
                    catch (Exception rowEx)
                    {
                        Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", $"Error processing row {i}: {rowEx.Message}");
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", "Error getting ITI data points: " + ex.Message);
        }

        return itiPoints;
    }

    private string GetTimeRangeDescription(int days)
    {
        if (days == 1) return "Last 24 Hours";
        if (days == 7) return "Last 7 Days";
        if (days == 30) return "Last 30 Days";
        return $"Last {days} Days";
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
            Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", "Error getting device names: " + ex.Message);
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
            Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", "Error in CollectDeviceNames: " + ex.Message);
        }
    }

    private void WriteITIDataJSON(object dashboardData)
    {
        try
        {
            var projectPath = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDir = projectPath.Uri;
            string resDir = Path.Combine(baseDir, "res", "TabCommon_ITIAndSEMIF47ChartDashboard");

            if (!Directory.Exists(resDir))
                Directory.CreateDirectory(resDir);

            string filePath = Path.Combine(resDir, "iti-chart-data.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dashboardData, options);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_ITIAndSEMIF47ChartAggregator", "Error writing JSON: " + ex.Message);
        }
    }

    private void WriteEmptyITIData()
    {
        string selectedPath = selectedOrgNodeVar?.Value ?? "";
        var emptyData = new
        {
            orgName = StripModelPrefix(selectedPath),
            deviceCount = 0,
            timeRange = "No Data",
            lastUpdate = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            points = new List<object>(),
            LastRefreshTimestamp = DateTime.Now.ToString("o")
        };

        WriteITIDataJSON(emptyData);
    }

    private void RefreshITIChartBrowser()
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("wb_ITIAndSEMIF47CharDashboard");
            if (webBrowser != null)
            {
                webBrowser.Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_ITIAndSEMIF47ChartAggregator", "Error refreshing browser: " + ex.Message);
        }
    }
}
