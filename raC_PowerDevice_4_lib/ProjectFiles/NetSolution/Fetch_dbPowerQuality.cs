using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.ODBCStore;
using FTOptix.RAEtherNetIP;
using FTOptix.MicroController;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Threading.Tasks;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;

public class Fetch_dbPowerQuality : BaseNetLogic
{
    private string deviceName;
    private LongRunningTask fetchTask;
    private PeriodicTask uiUpdateTask;
    private Label logLabel;
    private IUAVariable button_Animation;
    private IUAVariable New_event_triggered;
    private string lastErrorMessage;
    private int lastRecordCount;

    // Unified table name - must match the logger
    private const string UNIFIED_TABLE_NAME = "PowerQualityLog_AllDevices";

    public override void Start()
    {
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Fetch_dbPowerQuality", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Fetch_dbPowerQuality", "Device name is null or empty");
            return;
        }

        button_Animation = GetVariableValue("Button_Animation");
        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Fetch_dbPowerQuality", "Label 'log' not found");
            return;
        }

        New_event_triggered = Tag.GetVariable("db_PQLogger/NewEventTrigger");
        New_event_triggered.VariableChange += tab_refresh_VariableChange;

        SetupDeviceSpecificHtml();
        FetchLastWeek();
    }

    public override void Stop()
    {
        button_Animation.Value = 0;
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
        New_event_triggered.VariableChange -= tab_refresh_VariableChange;
    }

    private void UpdateUI()
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
                    : $"Found {lastRecordCount} records";
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
            Log.Error("Fetch_dbPowerQuality", $"Error in UpdateUI: {ex.Message}\nStackTrace: {ex.StackTrace}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void tab_refresh_VariableChange(object sender, VariableChangeEventArgs e)
    {
        FetchLastWeek();
    }

    private void ProcessPowerQualityLog_filter(string deviceName, DateTime? startDate = null, DateTime? endDate = null)
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
            Log.Warning("Fetch_dbPowerQuality", $"Device status is {cat}, fetch aborted.");
            logLabel.Text = $"Power Quality Logs not supported for: {deviceName} catalog";
            return;
        }

        lastErrorMessage = null;
        lastRecordCount = 0;
        logLabel.Text = "Fetching data...";
        fetchTask = new LongRunningTask(async task => await FetchAndSaveJson(task, deviceName, startDate, endDate), LogicObject);
        uiUpdateTask = new PeriodicTask(UpdateUI, 100, LogicObject);
        fetchTask.Start();
        uiUpdateTask.Start();
    }

    private async Task FetchAndSaveJson(LongRunningTask task, string deviceName, DateTime? startDate, DateTime? endDate)
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

            // Fetch data from unified table filtered by deviceName
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

            await SaveJsonData(powerQualityDataList, deviceName);
            fetchTask = null;
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbPowerQuality", $"Error in FetchAndSaveJson: {ex.Message}\nStackTrace: {ex.StackTrace}");
            lastErrorMessage = $"Error: {ex.Message}";
            fetchTask = null;
        }
    }

    private async Task SaveJsonData(List<PowerQualityData2> powerQualityDataList, string deviceName)
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res", "TabPowerQuality");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string filePath = Path.Combine(directory, $"{deviceName}_PQLogData.json");

            var jsonData = new
            {
                events = powerQualityDataList,
                LastRefreshTimestamp = DateTime.Now.ToString("o")
            };

            string jsonString = JsonConvert.SerializeObject(jsonData, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml,
                Formatting = Formatting.Indented
            });

            await File.WriteAllTextAsync(filePath, jsonString);
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Error("Fetch_dbPowerQuality", $"Permission denied when writing JSON file for {deviceName}: {e.Message}");
            lastErrorMessage = $"Error saving JSON: {e.Message}";
        }
        catch (IOException e)
        {
            Log.Error("Fetch_dbPowerQuality", $"IO error exporting to JSON for {deviceName}: {e.Message}");
            lastErrorMessage = $"Error saving JSON: {e.Message}";
        }
        catch (Exception e)
        {
            Log.Error("Fetch_dbPowerQuality", $"Unexpected error exporting to JSON for {deviceName}: {e.Message}");
            lastErrorMessage = $"Unexpected error: {e.Message}";
        }
    }

    private async Task SaveEmptyJson(string deviceName)
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res", "TabPowerQuality");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string filePath = Path.Combine(directory, $"{deviceName}_PQLogData.json");

            var jsonData = new
            {
                events = new object[0],
                LastRefreshTimestamp = DateTime.Now.ToString("o")
            };

            string jsonString = JsonConvert.SerializeObject(jsonData, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml,
                Formatting = Formatting.Indented
            });

            await File.WriteAllTextAsync(filePath, jsonString);
        }
        catch (Exception e)
        {
            Log.Error("Fetch_dbPowerQuality", $"Error exporting empty JSON for {deviceName}: {e.Message}");
            lastErrorMessage = $"Error: {e.Message}";
        }
    }

    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res", "TabPowerQuality");

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
            }

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_PowerQuality.html");
            string deviceHtmlPath = Path.Combine(resDirectory, $"Template_PowerQuality_{deviceName}.html");

            if (!File.Exists(htmlTemplatePath))
            {
                Log.Error("Fetch_dbPowerQuality", $"HTML template not found at {htmlTemplatePath}");
                lastErrorMessage = $"Error: HTML template not found";
                return;
            }

            string htmlContent = File.ReadAllText(htmlTemplatePath);
            if (!htmlContent.Contains("{{deviceName}}"))
            {
                Log.Warning("Fetch_dbPowerQuality", $"HTML template at {htmlTemplatePath} does not contain '{{{{deviceName}}}}' placeholder");
            }

            htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
            File.WriteAllText(deviceHtmlPath, htmlContent);

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser == null)
            {
                Log.Error("Fetch_dbPowerQuality", "WebBrowser control 'WebBrowser' not found");
                lastErrorMessage = $"Error: WebBrowser control not found";
                return;
            }

            var deviceHtmlUri = ResourceUri.FromProjectRelativePath($"res/TabPowerQuality/Template_PowerQuality_{deviceName}.html");
            webBrowser.URL = deviceHtmlUri;
        }
        catch (IOException e)
        {
            Log.Error("Fetch_dbPowerQuality", $"IO error creating HTML file for device {deviceName}: {e.Message}");
            lastErrorMessage = $"Error creating HTML: {e.Message}";
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Error("Fetch_dbPowerQuality", $"Permission denied when creating HTML file for {deviceName}: {e.Message}");
            lastErrorMessage = $"Error creating HTML: {e.Message}";
        }
        catch (Exception e)
        {
            Log.Error("Fetch_dbPowerQuality", $"Unexpected error setting up HTML for {deviceName}: {e.Message}");
            lastErrorMessage = $"Error setting up HTML: {e.Message}";
        }
    }

    /// <summary>
    /// Fetches filtered data from the unified table for the specific device
    /// </summary>
    private List<PowerQualityData2> FetchFilteredDataFromDatabase(Store store, string deviceName, DateTime? startDate, DateTime? endDate)
    {
        var powerQualityDataList = new List<PowerQualityData2>();

        try
        {
            // Query unified table with DeviceName filter
            string query = $"SELECT * FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{deviceName}'";
            
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
                var record = new PowerQualityData2
                {
                    Record_Identifier = resultSet[i, Array.IndexOf(header, "Record_Identifier")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Record_Identifier")]) : 0,
                    Event_Type = resultSet[i, Array.IndexOf(header, "Event_Type")]?.ToString() ?? "",
                    Event_Code = resultSet[i, Array.IndexOf(header, "Event_Code")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Event_Code")]) : 0,
                    Sub_Event_Code = resultSet[i, Array.IndexOf(header, "Sub_Event_Code")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Sub_Event_Code")]) : 0,
                    Sub_Event = resultSet[i, Array.IndexOf(header, "Sub_Event")]?.ToString() ?? "",
                    Local_Timestamp = ConvertToIsoFormat(SafeConvertDateTime(resultSet[i, Array.IndexOf(header, "Local_Timestamp")])),
                    Event_Duration_mS = resultSet[i, Array.IndexOf(header, "Event_Duration_mS")] != null ? Convert.ToSingle(resultSet[i, Array.IndexOf(header, "Event_Duration_mS")]) : 0f,
                    Trip_Point = resultSet[i, Array.IndexOf(header, "Trip_Point")]?.ToString() ?? "",
                    Min_or_Max = resultSet[i, Array.IndexOf(header, "Min_or_Max")]?.ToString() ?? "",
                    Association_Timestamp = ConvertToIsoFormat(SafeConvertDateTime(resultSet[i, Array.IndexOf(header, "Association_Timestamp")]))
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

    /// <summary>
    /// Safely converts a datetime value from database result, handling invalid dates
    /// </summary>
    private string SafeConvertDateTime(object value)
    {
        if (value == null)
            return "";

        string strValue = value.ToString();
        
        // Check for invalid datetime patterns (e.g., "0-00-00 00:00:00.000000")
        if (string.IsNullOrWhiteSpace(strValue) || strValue.StartsWith("0-00-00"))
            return "";

        return strValue;
    }

    /// <summary>
    /// Converts timestamp from database format to ISO 8601 format
    /// Handles formats like "YYYY-MM-DD HH:MM:SS.ffffff", "DD-MM-YYYY HH:MM:SS", and others
    /// </summary>
    private string ConvertToIsoFormat(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return "";

        try
        {
            // Try multiple parsing approaches
            DateTime dt;
            
            // Try invariant culture first (YYYY-MM-DD format)
            if (DateTime.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt))
            {
                return dt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
            }
            
            // Try en-GB culture (DD-MM-YYYY format)
            if (DateTime.TryParse(timestamp, System.Globalization.CultureInfo.GetCultureInfo("en-GB"), System.Globalization.DateTimeStyles.None, out dt))
            {
                return dt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
            }
            
            // Try current system culture
            if (DateTime.TryParse(timestamp, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out dt))
            {
                return dt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
            }
            
            Log.Warning("Fetch_dbPowerQuality", $"Could not parse timestamp with any format: {timestamp}");
        }
        catch (Exception ex)
        {
            Log.Warning("Fetch_dbPowerQuality", $"Error converting timestamp '{timestamp}': {ex.Message}");
        }
        
        return "";
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
        DateTime startDate = DateTime.Now.AddHours(-24);
        DateTime endDate = DateTime.Now;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        button_Animation.Value = 1;
    }

    [ExportMethod]
    public void FetchLastWeek()
    {
        DateTime startDate = DateTime.Now.AddDays(-7);
        DateTime endDate = DateTime.Now;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        button_Animation.Value = 2;
    }

    [ExportMethod]
    public void FetchLastMonth()
    {
        DateTime startDate = DateTime.Now.AddMonths(-1);
        DateTime endDate = DateTime.Now;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        button_Animation.Value = 3;
    }

    [ExportMethod]
    public void FetchLast6Months()
    {
        DateTime startDate = DateTime.Now.AddMonths(-6);
        DateTime endDate = DateTime.Now;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        button_Animation.Value = 4;
    }

    [ExportMethod]
    public void FetchLastYear()
    {
        DateTime startDate = DateTime.Now.AddYears(-1);
        DateTime endDate = DateTime.Now;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
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
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        button_Animation.Value = 6;
    }
}

public class PowerQualityData2
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
