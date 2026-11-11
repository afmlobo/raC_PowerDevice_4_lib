using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.Core;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FTOptix.WebUI;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using System.Threading.Tasks;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;

public class Fetch_dbITICurve : BaseNetLogic
{
    private string deviceName;
    private LongRunningTask fetchTask;
    private PeriodicTask uiUpdateTask;
    private Label logLabel;
    private IUAVariable button_Animation;
    private string timeRangeLabel;
    private string lastErrorMessage;
    private int lastRecordCount;

    // Unified table name - must match the logger
    private const string UNIFIED_TABLE_NAME = "PowerQualityLog_AllDevices";

    public override void Start()
    {
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Fetch_dbITICurve", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Fetch_dbITICurve", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Fetch_dbITICurve", "Label 'log' not found");
            return;
        }

        button_Animation = GetVariableValue("Button_Animation");
        //timeRangeLabel = GetVariableValue("TimeRangeLabel");
        SetupDeviceSpecificHtml();
        FetchLastWeek();
    }

    public override void Stop()
    {
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }
        if (uiUpdateTask != null)
        {
            uiUpdateTask.Dispose();
            uiUpdateTask = null;
        }
        button_Animation.Value = 0;
    }

    private void ProcessITICurveData(string deviceName, DateTime? startDate, DateTime? endDate)
    {
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }
        if (uiUpdateTask != null)
        {
            uiUpdateTask.Dispose();
            uiUpdateTask = null;
        }

        IUAVariable device_cat = Owner.GetAlias("Tag").GetVariable("DeviceStatus/CatalogNumber");
        string cat = device_cat.Value;

        if (cat == "1426-M5")
        {
            Log.Warning("Fetch_dbITICurve", $"Device status is {cat}, fetch aborted.");
            logLabel.Text = $"ITI Curve not supported for: {deviceName} catalog";
            return;
        }

        lastErrorMessage = null;
        lastRecordCount = 0;
        logLabel.Text = "Starting ITI curve data fetch...";
        fetchTask = new LongRunningTask(async task => await FetchAndUpdateITICurve(task, deviceName, startDate, endDate), LogicObject);
        uiUpdateTask = new PeriodicTask(CheckTaskCompletionAndUpdateUI, 100, LogicObject);
        fetchTask.Start();
        uiUpdateTask.Start();
    }

    private async Task FetchAndUpdateITICurve(LongRunningTask task, string deviceName, DateTime? startDate, DateTime? endDate)
    {
        try
        {
            var project = FTOptix.HMIProject.Project.Current;
            var pqStore = project.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (task.IsCancellationRequested) return;

            // Check if unified table exists
            if (!TableExists(pqStore, UNIFIED_TABLE_NAME))
            {
                Log.Error($"Unified table '{UNIFIED_TABLE_NAME}' does not exist in the database.");
                lastErrorMessage = $"Error: Unified data table not found";
                await SaveEmptyJson(deviceName);
                fetchTask = null;
                return;
            }

            // Fetch data from unified table filtered by deviceName AND event types
            var powerQualityDataList = FetchFilteredDataFromDatabase(pqStore, deviceName, startDate, endDate);
            lastRecordCount = powerQualityDataList.Count;

            if (powerQualityDataList.Count == 0)
            {
                lastErrorMessage = $"No records found for {deviceName} between {startDate?.ToString("yyyy-MM-dd") ?? "All Dates"} and {endDate?.ToString("yyyy-MM-dd") ?? "All Dates"}";
                await SaveEmptyJson(deviceName);
                fetchTask = null;
                return;
            }

            if (task.IsCancellationRequested) return;

            var itiCurveData = ProcessPowerQualityLogs(powerQualityDataList);
            var processedDataAsLists = new
            {
                deviceName = deviceName,
                timeRange = timeRangeLabel,
                eventFilter = "IEEE1159",
                points = GroupDataByEventType(itiCurveData),
                summary = new
                {
                    IEEE1159_Count = itiCurveData.FindAll(x => x.EventType.Contains("IEEE1159")).Count,
                    UserDefined_Count = itiCurveData.FindAll(x => !x.EventType.Contains("IEEE1159")).Count,
                    Total_Count = itiCurveData.Count
                },
                LastRefreshTimestamp =  DateTime.Now.ToString("o")
            };

            await SaveJsonData(processedDataAsLists, deviceName);
            await Task.Delay(100);
            lastRecordCount = itiCurveData.Count;
            fetchTask = null;
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbITICurve", $"Error in FetchAndUpdateITICurve: {ex.Message}\nStackTrace: {ex.StackTrace}");
            lastErrorMessage = $"Error: {ex.Message}";
            fetchTask = null;
        }
    }

    private void CheckTaskCompletionAndUpdateUI()
    {
        if (fetchTask != null && !fetchTask.IsCancellationRequested)
            return;

        try
        {
            if (!string.IsNullOrEmpty(lastErrorMessage))
            {
                logLabel.Text = lastErrorMessage;
            }
            else
            {
                logLabel.Text = lastRecordCount == 0
                    ? $"No records found for {deviceName}"
                    : $"Processed {lastRecordCount} ITI curve points";
            }

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser != null)
            {
                webBrowser.Opacity = 100;
                webBrowser.Refresh();
            }

            if (uiUpdateTask != null)
            {
                uiUpdateTask.Dispose();
                uiUpdateTask = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbITICurve", $"Error in CheckTaskCompletionAndUpdateUI: {ex.Message}\nStackTrace: {ex.StackTrace}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task SaveJsonData(object data, string deviceName)
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res", "TabITICurve");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string filePath = Path.Combine(directory, $"{deviceName}_ITICurveData.json");

            string jsonString = JsonConvert.SerializeObject(data, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml,
                Formatting = Formatting.Indented
            });

            await File.WriteAllTextAsync(filePath, jsonString);
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbITICurve", $"Error saving JSON for {deviceName}: {ex.Message}\nStackTrace: {ex.StackTrace}");
            lastErrorMessage = $"Error saving JSON: {ex.Message}";
        }
    }

    private async Task SaveEmptyJson(string deviceName)
    {
        var emptyData = new
        {
            points = new List<object>(),
            LastRefreshTimestamp =  DateTime.Now.ToString("o")
        };
        await SaveJsonData(emptyData, deviceName);
    }

    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res", "TabITICurve");

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
            }

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_ITIDashboard.html");
            string deviceHtmlPath = Path.Combine(resDirectory, $"Template_ITIDashboard_{deviceName}.html");

            if (!File.Exists(htmlTemplatePath))
            {
                Log.Error("Fetch_dbITICurve", $"HTML template not found at {htmlTemplatePath}");
                return;
            }

            string htmlContent = File.ReadAllText(htmlTemplatePath);
            htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
            File.WriteAllText(deviceHtmlPath, htmlContent);

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser == null)
            {
                Log.Error("Fetch_dbITICurve", "WebBrowser control 'WebBrowser' not found");
                return;
            }

            var deviceHtmlUri = ResourceUri.FromProjectRelativePath($"res/TabITICurve/Template_ITIDashboard_{deviceName}.html");
            webBrowser.URL = deviceHtmlUri;
            webBrowser.Opacity = 100;
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbITICurve", $"Error setting up HTML for {deviceName}: {ex.Message}\nStackTrace: {ex.StackTrace}");
            logLabel.Text = $"Error setting up HTML: {ex.Message}";
        }
    }

    private List<(double Cycles, double Percentage, string EventType, string SubEvent, int SubEventCode, string Timestamp)> ProcessPowerQualityLogs(List<PowerQualityData3> logs)
    {
        var processedData = new List<(double Cycles, double Percentage, string EventType, string SubEvent, int SubEventCode, string Timestamp)>();

        foreach (var log in logs)
        {
            try
            {
                double seconds = log.Event_Duration_mS / 1000.0;
                double cycles = seconds * 60; // 60 Hz
                string minOrMaxStr = log.Min_or_Max?.Replace("%", "").Trim() ?? "0";
                if (!float.TryParse(minOrMaxStr, out float minOrMax))
                {
                    Log.Warning("Fetch_dbITICurve", $"Failed to parse Min_or_Max '{log.Min_or_Max}' for Record_Identifier {log.Record_Identifier}, defaulting to 0");
                    minOrMax = 0;
                }
                double nominalVoltage = 480;
                double absoluteDifference = Math.Abs(minOrMax - nominalVoltage);
                double percentage = (absoluteDifference / nominalVoltage) * 100;
                processedData.Add((cycles, percentage, log.Event_Type, log.Sub_Event, log.Sub_Event_Code, log.Local_Timestamp));
            }
            catch (Exception ex)
            {
                Log.Error("Fetch_dbITICurve", $"Error processing log Record_Identifier {log.Record_Identifier}: {ex.Message}");
            }
        }

        return processedData;
    }

    /// <summary>
    /// Fetches filtered data from the unified table for the specific device
    /// Filters for IEEE 1159 events and Voltage events only
    /// </summary>
    private List<PowerQualityData3> FetchFilteredDataFromDatabase(Store store, string deviceName, DateTime? startDate, DateTime? endDate)
    {
        var powerQualityDataList = new List<PowerQualityData3>();

        try
        {
            // Query unified table with DeviceName filter AND specific event types
            string query = $"SELECT * FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{deviceName}' " +
                          "AND (Event_Type IN ('IEEE1159_Swell', 'IEEE1159_Sag', 'Voltage_Swell', 'Voltage_Sag'))";
            
            if (startDate.HasValue && endDate.HasValue)
            {
                query += $" AND Local_Timestamp BETWEEN '{startDate.Value.ToString("yyyy-MM-ddTHH:mm:ss")}' AND '{endDate.Value.ToString("yyyy-MM-ddTHH:mm:ss")}'";
            }
            
            query += " ORDER BY Local_Timestamp DESC";

            object[,] resultSet;
            string[] header;
            store.Query(query, out header, out resultSet);

            if (resultSet == null || resultSet.GetLength(0) == 0)
            {
                return powerQualityDataList;
            }

            // Note: DeviceName column is now included in the result, but we don't need to use it
            string[] expectedColumns = { "DeviceName", "Record_Identifier", "Event_Type", "Event_Code", "Sub_Event_Code", "Sub_Event", 
                                        "Local_Timestamp", "Event_Duration_mS", "Trip_Point", "Min_or_Max", "Association_Timestamp" };
            
            foreach (var column in expectedColumns)
            {
                if (Array.IndexOf(header, column) == -1)
                {
                    Log.Error($"Column '{column}' not found in query result.");
                    return powerQualityDataList;
                }
            }

            for (int i = 0; i < resultSet.GetLength(0); i++)
            {
                var record = new PowerQualityData3
                {
                    Record_Identifier = resultSet[i, Array.IndexOf(header, "Record_Identifier")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Record_Identifier")]) : 0,
                    Event_Type = resultSet[i, Array.IndexOf(header, "Event_Type")]?.ToString() ?? "",
                    Event_Code = resultSet[i, Array.IndexOf(header, "Event_Code")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Event_Code")]) : 0,
                    Sub_Event_Code = resultSet[i, Array.IndexOf(header, "Sub_Event_Code")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Sub_Event_Code")]) : 0,
                    Sub_Event = resultSet[i, Array.IndexOf(header, "Sub_Event")]?.ToString() ?? "",
                    Local_Timestamp = resultSet[i, Array.IndexOf(header, "Local_Timestamp")]?.ToString() ?? "",
                    Event_Duration_mS = resultSet[i, Array.IndexOf(header, "Event_Duration_mS")] != null ? Convert.ToSingle(resultSet[i, Array.IndexOf(header, "Event_Duration_mS")]) : 0f,
                    Trip_Point = resultSet[i, Array.IndexOf(header, "Trip_Point")]?.ToString() ?? "",
                    Min_or_Max = resultSet[i, Array.IndexOf(header, "Min_or_Max")]?.ToString() ?? "",
                    Association_Timestamp = resultSet[i, Array.IndexOf(header, "Association_Timestamp")]?.ToString() ?? ""
                };
                powerQualityDataList.Add(record);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error fetching filtered data from database: {ex.Message}");
            lastErrorMessage = $"Error fetching data: {ex.Message}";
        }

        return powerQualityDataList;
    }

    private bool TableExists(Store store, string tableName)
    {
        object[,] resultSet;
        string[] header;

        try
        {
            store.Query($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'", out header, out resultSet);
            if (resultSet != null && resultSet.GetLength(0) > 0)
            {
                int tableExists = Convert.ToInt32(resultSet[0, 0]);
                return tableExists > 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error checking if table '{tableName}' exists: {ex.Message}");
            lastErrorMessage = $"Error checking table: {ex.Message}";
        }

        return false;
    }

    private IUAVariable GetVariableValue(string variableName)
    {
        var variable = LogicObject.GetVariable(variableName);
        if (variable == null)
        {
            Log.Error($"{variableName} not found");
            return null;
        }
        return variable;
    }

    [ExportMethod]
    public void FetchLast24Hours()
    {
        timeRangeLabel = "Last 24 Hours";
        DateTime startDate =  DateTime.Now.AddHours(-24);
        DateTime endDate = DateTime.Now;
        ProcessITICurveData(deviceName, startDate, endDate);
        button_Animation.Value = 1;
    }

    [ExportMethod]
    public void FetchLastWeek()
    {
        timeRangeLabel = "Last 7 Days";
        DateTime startDate =  DateTime.Now.AddDays(-7);
        DateTime endDate = DateTime.Now;
        ProcessITICurveData(deviceName, startDate, endDate);
        button_Animation.Value = 2;
    }

    [ExportMethod]
    public void FetchLastMonth()
    {
        timeRangeLabel = "Last 30 Days";
        DateTime startDate =  DateTime.Now.AddMonths(-1);
        DateTime endDate = DateTime.Now;
        ProcessITICurveData(deviceName, startDate, endDate);
        button_Animation.Value = 3;
    }

    [ExportMethod]
    public void FetchLast6Months()
    {
        timeRangeLabel = "Last 6 Months";
        DateTime startDate =  DateTime.Now.AddMonths(-6);
        DateTime endDate = DateTime.Now;
        ProcessITICurveData(deviceName, startDate, endDate);
        button_Animation.Value = 4;
    }

    [ExportMethod]
    public void FetchLastYear()
    {
        timeRangeLabel = "Last Year";
        DateTime startDate =  DateTime.Now.AddYears(-1);
        DateTime endDate = DateTime.Now;
        ProcessITICurveData(deviceName, startDate, endDate);
        button_Animation.Value = 5;
    }

    [ExportMethod]
    public void FetchCustomRange(DateTime startDate, DateTime endDate)
    {
        if (startDate > endDate)
        {
            Log.Error("FetchCustomRange", "Start date must be earlier than end date.");
            logLabel.Text = "Error: Start date must be earlier than end date";
            return;
        }
        timeRangeLabel = $"Custom: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}";
        ProcessITICurveData(deviceName, startDate, endDate);
        button_Animation.Value = 6;
    }

  

    /// <summary>
    /// Group processed data by event type (IEEE1159 vs UserDefined)
    /// Includes Sub Event information (V1, V2, V3)
    /// </summary>
    private object GroupDataByEventType(List<(double Cycles, double Percentage, string EventType, string SubEvent, int SubEventCode, string Timestamp)> data)
    {
        var ieee1159Points = new List<object>();
        var userDefinedPoints = new List<object>();

        foreach (var item in data)
        {
            // Filter out zero cycles
            if (item.Cycles <= 0) continue;

            // Extract sub-event display name (V1, V2, V3, etc.)
            string subEventDisplay = ExtractSubEventDisplay(item.SubEvent, item.SubEventCode);

            var point = new
            {
                Cycles = Math.Round(item.Cycles, 2),
                Percentage = Math.Round(item.Percentage, 2),
                EventType = item.EventType,
                SubEvent = item.SubEvent,
                SubEventCode = item.SubEventCode,
                SubEventDisplay = subEventDisplay,
                Timestamp = item.Timestamp
            };

            if (item.EventType.Contains("IEEE1159"))
            {
                ieee1159Points.Add(point);
            }
            else
            {
                userDefinedPoints.Add(point);
            }
        }

        return new
        {
            IEEE1159 = ieee1159Points,
            UserDefined = userDefinedPoints
        };
    }

    /// <summary>
    /// Extract display name for sub-event (V1, V2, V3, etc.)
    /// </summary>
    private string ExtractSubEventDisplay(string subEvent, int subEventCode)
    {
        if (string.IsNullOrWhiteSpace(subEvent))
            return "";

        // If sub event already contains V1, V2, V3 format, extract it
        if (subEvent.Contains("V1") || subEvent.Contains("V2") || subEvent.Contains("V3"))
        {
            // Extract the V* part: "V1_Swell" -> "V1", "V2_Sag" -> "V2"
            var match = System.Text.RegularExpressions.Regex.Match(subEvent, @"V\d");
            if (match.Success)
                return match.Value;
        }

        // Fallback: Use sub event code to generate V1, V2, V3
        if (subEventCode > 0)
            return $"V{subEventCode}";

        return subEvent;
    }


    public class PowerQualityData3
{
    public int Record_Identifier { get; set; }
    public string Event_Type { get; set; }
    public int Event_Code { get; set; }
    public int Sub_Event_Code { get; set; }
    public string Sub_Event { get; set; }
    public string Local_Timestamp { get; set; }
    public float Event_Duration_mS { get; set; }
    public string Trip_Point { get; set; }
    public string Min_or_Max { get; set; }
    public string Association_Timestamp { get; set; }
}
}
