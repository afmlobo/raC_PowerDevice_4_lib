#region Using directives
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.Core;
using FTOptix.SQLiteStore;
using FTOptix.SerialPort;
#endregion

public class db_PQLogger : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private IUAVariable logLabel;
    private IUAVariable _newEventTrigger;
    private LongRunningTask downloadTask;
    private LongRunningTask processTask;
    private PeriodicTask periodicTask;
    private IUAVariable updatePeriod;
    private IUAVariable enablePQEventsCheck;
    private HttpClient httpClient;
    private DateTime lastDeviceReadyCheck = DateTime.MinValue;
    private bool lastDeviceReadyStatus = false;

    private const string HTTP_REMOTE_DIR = "/LoggingResults";
    private const string UNIFIED_TABLE_NAME = "PowerQualityLog_AllDevices";

    public db_PQLogger()
    {
        httpClient = new HttpClient(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public override void Start()
    {
        _newEventTrigger = LogicObject.GetVariable("NewEventTrigger");

        deviceName = Owner.BrowseName;
        if (string.IsNullOrEmpty(deviceName)) { Log.Error($"db_PQLogger-{deviceName}", "Device name is null or empty."); return; }

        var ipVariable = Owner.GetVariable("Val_IPAddress");
        if (ipVariable == null) { Log.Error($"db_PQLogger-{deviceName}", "Val_IPAddress variable not found."); return; }
        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress)) { Log.Error($"db_PQLogger-{deviceName}", "IP address is null or empty."); return; }

        logLabel = LogicObject.GetVariable("logLabel");
        if (logLabel == null)
        {
            logLabel = InformationModel.MakeVariable("logLabel", OpcUa.DataTypes.String);
            LogicObject.Add(logLabel);
        }

        if (_newEventTrigger == null)
        {
            _newEventTrigger = InformationModel.MakeVariable("NewEventTrigger", OpcUa.DataTypes.Boolean);
            
            LogicObject.Add(_newEventTrigger);
            _newEventTrigger.Value = false;
        }

        // Initialize periodic task variables
        updatePeriod = Owner.GetVariable("Cfg_PQEventsUpdatePeriod");
        if (updatePeriod == null)
        {
            Log.Error($"db_PQLogger-{deviceName}", "Cfg_PQEventsUpdatePeriod variable not found.");
            return;
        }

        enablePQEventsCheck = Owner.GetVariable("Set_EnablePQEventsCheck");
        if (enablePQEventsCheck == null)
        {
            Log.Error($"db_PQLogger-{deviceName}", "Set_EnablePQEventsCheck variable not found.");
            return;
        }

        logLabel.Value = $"Ready to refresh data for {deviceName}";

        // Ensure unified table exists
        EnsureUnifiedTableExists();

        // Subscribe to enable/disable changes
        enablePQEventsCheck.VariableChange += EnablePQEventsCheck_VariableChange;
        
        // Initialize periodic task (HTTP-based, no FTP coordinator needed)
        InitializePeriodicTask();
    }

    public override void Stop()
    {
        downloadTask?.Dispose();
        processTask?.Dispose();
        periodicTask?.Dispose();
        downloadTask = null;
        processTask = null;
        periodicTask = null;
        if (enablePQEventsCheck != null)
            enablePQEventsCheck.VariableChange -= EnablePQEventsCheck_VariableChange;
    }

    /// <summary>
    /// Check if device is ready by attempting to fetch overview page
    /// This is a direct HTTP check independent of cached DeviceStatus variables
    /// </summary>
    private bool IsDeviceReady()
    {
        try
        {
            var response = httpClient.GetAsync($"http://{ipAddress}/overview.shtm").Result;
            bool isReady = response.IsSuccessStatusCode;

            // Log status transitions only (avoid spamming logs)
            if (isReady != lastDeviceReadyStatus)
            {
                Log.Debug($"db_PQLogger-{deviceName}", isReady ? "Device is now Ready" : "Device is Not Ready");
                lastDeviceReadyStatus = isReady;
            }

            lastDeviceReadyCheck = DateTime.Now;
            return isReady;
        }
        catch (Exception ex)
        {
            if (lastDeviceReadyStatus)
            {
                Log.Debug($"db_PQLogger-{deviceName}", $"Device communication failed: {ex.Message}");
                lastDeviceReadyStatus = false;
            }

            lastDeviceReadyCheck = DateTime.Now;
            return false;
        }
    }

    [ExportMethod]
    public void DownloadPowerQualityLogAsync()
    {
        // Check if IP address is set
        if (string.IsNullOrEmpty(ipAddress))
        {
            if (logLabel != null)
                logLabel.Value = "Cannot refresh: IP address not set.";
            Log.Error($"db_PQLogger-{deviceName}", "IP address not set, cannot start download.");
            return;
        }

        // Check device status and catalog number
        IUAVariable device_status = Owner.GetVariable("DeviceStatus/Status");
        IUAVariable device_cat = Owner.GetVariable("DeviceStatus/CatalogNumber");

        // Validate device_cat exists first, then access its value
        if (device_cat == null || device_cat.Value == null)
        {
            if (logLabel != null)
                logLabel.Value = $"Ready to refresh data for {deviceName}";
            Log.Warning($"db_PQLogger-{deviceName}", $"Device catalog number not found, cannot proceed with download.");
            return;
        }

        string device_catval = device_cat.Value.Value?.ToString() ?? "unknown";

        if (device_catval == "1426-M5")
        {
            if (logLabel != null)
                logLabel.Value = $"Device {deviceName} ({device_catval}) - PQ events not supported";
            Log.Warning($"db_PQLogger-{deviceName}", $"Device Catalog is {device_catval}, download aborted.");
            return;
        }

        // Proceed with download if checks pass
        downloadTask?.Dispose();
        downloadTask = new LongRunningTask(DownloadTaskMethod, LogicObject);
        downloadTask.Start();
    }

    [ExportMethod]
    public void ProcessPowerQualityLogAsync(string deviceNameOverride)
    {
        string effectiveDeviceName = string.IsNullOrEmpty(deviceNameOverride) ? deviceName : deviceNameOverride;
        if (string.IsNullOrEmpty(effectiveDeviceName))
        {
            logLabel.Value = "Cannot process: Device name not provided.";
            Log.Error($"db_PQLogger-{deviceName}", "Device name is null or empty.");
            return;
        }

        processTask?.Dispose();
        processTask = new LongRunningTask(() => ProcessPowerQualityLogTask(effectiveDeviceName), LogicObject);
        processTask.Start();
    }

    private void DownloadTaskMethod()
    {
        string remoteFilePath = "Power_Quality_Log.csv";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, "res\\TabPowerQuality", deviceName);
        string localFilePath = Path.Combine(localFolder, "Power_Quality_Log.csv");

        try
        {
            // Defensive null checks
            if (string.IsNullOrEmpty(ipAddress))
            {
                Log.Error($"db_PQLogger-{deviceName}", "IP address is not set, cannot proceed with download.");
                return;
            }

            if (logLabel == null)
            {
                Log.Error($"db_PQLogger-{deviceName}", "Log label variable is not initialized, cannot proceed with download.");
                return;
            }

            Directory.CreateDirectory(localFolder);
            logLabel.Value = $"Refreshing events for {deviceName}...";

            try
            {
                string url = $"http://{ipAddress}{HTTP_REMOTE_DIR}/{remoteFilePath}";
                byte[] fileBytes = httpClient.GetByteArrayAsync(url).Result;

                File.WriteAllBytes(localFilePath, fileBytes);
                logLabel.Value = $"Events downloaded for {deviceName}. Processing now...";
                ProcessPowerQualityLogAsync(deviceName);
            }
            catch (HttpRequestException ex)
            {
                logLabel.Value = $"Refresh failed: Cannot connect to {deviceName}.";
                Log.Error($"db_PQLogger-{deviceName}", $"HTTP error: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                logLabel.Value = $"Refresh timed out for {deviceName}.";
                Log.Error($"db_PQLogger-{deviceName}", $"Download timed out");
            }
            catch (Exception ex)
            {
                logLabel.Value = $"Refresh error: {ex.Message}";
                Log.Error($"db_PQLogger-{deviceName}", $"Error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            if (logLabel != null)
                logLabel.Value = $"Refresh error: {ex.Message}";
            Log.Error($"db_PQLogger-{deviceName}", $"Error downloading Power Quality Log: {ex.Message}");
        }
    }

    private void ProcessPowerQualityLogTask(string deviceName)
    {
        try
        {
            _newEventTrigger.Value = false;
            logLabel.Value = $"Checking for new events on {deviceName}...";

            string csvFilePath = Path.Combine(new ResourceUri("%PROJECTDIR%\\").Uri,"res\\TabPowerQuality", deviceName,"Power_Quality_Log.csv");
            if (!File.Exists(csvFilePath))
            {
                logLabel.Value = $"Refresh stopped: Event file not found for {deviceName}.";
                Log.Error($"db_PQLogger-{deviceName}", $"File not found at: {csvFilePath}");
                return;
            }

            var pqStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            
            // Ensure unified table exists
            EnsureUnifiedTableExists();

            var powerQualityDataList = ParseCsvFile(csvFilePath);
            DateTime latestTimestampInDb = GetLatestTimestampForDevice(pqStore, deviceName);

            var newEvents = powerQualityDataList
                .Where(record => DateTime.Parse(record.Local_Timestamp) > latestTimestampInDb)
                .ToList();

            if (newEvents.Count > 0)
            {
                logLabel.Value = $"Found {newEvents.Count} new events for {deviceName}. Saving...";
                LogNewEventsToDatabase(pqStore, deviceName, newEvents);
                
                // Create the JSON file for new events
                var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
                string baseDirectory = filePathValue.Uri;
                string jsonDir = Path.Combine(baseDirectory, deviceName, "NewEventsDetection");
                string jsonFilePath = Path.Combine(jsonDir, $"{deviceName}_EventsQueue.json");

                // Ensure the directory exists
                Directory.CreateDirectory(jsonDir);

                // Serialize new events to JSON and save
                string json = System.Text.Json.JsonSerializer.Serialize(newEvents, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonFilePath, json);

                _newEventTrigger.Value = true;
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                logLabel.Value = $"Refresh complete: {newEvents.Count} new events added for {deviceName}. Last refresh: {timestamp}";
            }
            else
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                logLabel.Value = $"Refresh complete: No new events found for {deviceName}. Last refresh: {timestamp}";
            }
        }
        catch (Exception ex)
        {
            logLabel.Value = $"Refresh error: {ex.Message}";
            Log.Error($"db_PQLogger-{deviceName}", $"Error in ProcessPowerQualityLogTask: {ex.Message}");
        }
    }

    private void InitializePeriodicTask()
    {
        if (enablePQEventsCheck.Value && periodicTask == null)
        {
            periodicTask = new PeriodicTask(() =>
            {
                try
                {
                    // Defensive checks before calling download method
                    if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(ipAddress) || logLabel == null)
                    {
                        Log.Warning($"db_PQLogger-{deviceName ?? "unknown"}", "Required variables not initialized, skipping download.");
                        return;
                    }

                    // Check device readiness (independent check each cycle)
                    if (!IsDeviceReady())
                    {
                        return; // Skip this cycle, will check again next time
                    }

                    DownloadPowerQualityLogAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"db_PQLogger-{deviceName}", $"Error in periodic task: {ex.Message}");
                }
            }, updatePeriod.Value, LogicObject);
            periodicTask.Start();
        }
    }

    private void EnablePQEventsCheck_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if (e.NewValue)
        {
            if (periodicTask == null)
            {
                InitializePeriodicTask();
            }
        }
        else
        {
            if (periodicTask != null)
            {
                periodicTask.Dispose();
                periodicTask = null;
            }
        }
    }

    private List<PowerQualityData> ParseCsvFile(string csvFilePath)
    {
        try
        {
            var lines = File.ReadAllLines(csvFilePath);
            return lines.Skip(1)
                .Select(line =>
                {
                    var row = line.Split(',');
                    return new PowerQualityData
                    {
                        Record_Identifier = int.Parse(row[0]),
                        Event_Type = row[1],
                        Event_Code = GetEventCodeFromDictionary(row[1]),
                        Sub_Event_Code = int.Parse(row[2]),
                        Sub_Event = GetSubEventDescriptionFromDictionary(row[1], int.Parse(row[2])),
                        Local_Timestamp = FormatTimestamp(row[3], row[4], row[5], row[6]),
                        Event_Duration_mS = float.Parse(row[18]),
                        Trip_Point = row[20],
                        Min_or_Max = row[19],
                        Association_Timestamp = FormatTimestamp(row[13], row[14], row[15], row[16], row[17])
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            logLabel.Value = $"Refresh error: Issue reading event file.";
            Log.Error($"db_PQLogger-{deviceName}", $"Error parsing CSV file {csvFilePath}: {ex.Message}");
            return new List<PowerQualityData>();
        }
    }

    /// <summary>
    /// Gets the latest timestamp for a specific device from the unified table
    /// </summary>
    private DateTime GetLatestTimestampForDevice(Store store, string deviceName)
    {
        try
        {
            string query = $"SELECT MAX(Local_Timestamp) FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{deviceName}'";
            store.Query(query, out _, out object[,] resultSet);
            return resultSet != null && resultSet.GetLength(0) > 0 && resultSet[0, 0] != DBNull.Value
                ? Convert.ToDateTime(resultSet[0, 0])
                : DateTime.MinValue;
        }
        catch (Exception ex)
        {
            Log.Error($"db_PQLogger-{deviceName}", $"Error getting latest timestamp for device: {ex.Message}");
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Logs new events to the unified table with DeviceName column
    /// </summary>
    private void LogNewEventsToDatabase(Store store, string deviceName, List<PowerQualityData> newEvents)
    {
        string[] columns = { "DeviceName", "Record_Identifier", "Event_Type", "Event_Code", "Sub_Event_Code", "Sub_Event", "Local_Timestamp", "Event_Duration_mS", "Trip_Point", "Min_or_Max", "Association_Timestamp" };
        var rawValues = new object[newEvents.Count, columns.Length];

        for (int i = 0; i < newEvents.Count; i++)
        {
            rawValues[i, 0] = deviceName; // Add DeviceName as first column
            rawValues[i, 1] = newEvents[i].Record_Identifier;
            rawValues[i, 2] = newEvents[i].Event_Type;
            rawValues[i, 3] = newEvents[i].Event_Code;
            rawValues[i, 4] = newEvents[i].Sub_Event_Code;
            rawValues[i, 5] = newEvents[i].Sub_Event;
            rawValues[i, 6] = newEvents[i].Local_Timestamp;
            rawValues[i, 7] = newEvents[i].Event_Duration_mS;
            rawValues[i, 8] = newEvents[i].Trip_Point;
            rawValues[i, 9] = newEvents[i].Min_or_Max;
            rawValues[i, 10] = newEvents[i].Association_Timestamp;
        }

        try
        {
            var table = store.Tables.Get<Table>(UNIFIED_TABLE_NAME);
            if (table == null)
            {
                EnsureUnifiedTableExists();
                table = store.Tables.Get<Table>(UNIFIED_TABLE_NAME);
            }
            table.Insert(columns, rawValues);
        }
        catch (Exception ex)
        {
            logLabel.Value = $"Refresh error: Could not save new events.";
            Log.Error($"db_PQLogger-{deviceName}", $"Error logging events to {UNIFIED_TABLE_NAME}: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the unified table with DeviceName column if it doesn't exist
    /// </summary>
    private void EnsureUnifiedTableExists()
    {
        try
        {
            var pqStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            
            if (!TableExists(pqStore, UNIFIED_TABLE_NAME))
            {
                CreateUnifiedTable(pqStore);
                Log.Debug($"db_PQLogger-{deviceName}", $"Created unified table: {UNIFIED_TABLE_NAME}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"db_PQLogger-{deviceName}", $"Error ensuring unified table exists: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the unified table structure with DeviceName as first column
    /// </summary>
    private void CreateUnifiedTable(Store store)
    {
        store.AddTable(UNIFIED_TABLE_NAME);
        store.AddColumn(UNIFIED_TABLE_NAME, "DeviceName", OpcUa.DataTypes.String); // First column for device identification
        store.AddColumn(UNIFIED_TABLE_NAME, "Record_Identifier", OpcUa.DataTypes.Int32);
        store.AddColumn(UNIFIED_TABLE_NAME, "Event_Type", OpcUa.DataTypes.String);
        store.AddColumn(UNIFIED_TABLE_NAME, "Event_Code", OpcUa.DataTypes.Int32);
        store.AddColumn(UNIFIED_TABLE_NAME, "Sub_Event_Code", OpcUa.DataTypes.Int32);
        store.AddColumn(UNIFIED_TABLE_NAME, "Sub_Event", OpcUa.DataTypes.String);
        store.AddColumn(UNIFIED_TABLE_NAME, "Local_Timestamp", OpcUa.DataTypes.DateTime);
        store.AddColumn(UNIFIED_TABLE_NAME, "Event_Duration_mS", OpcUa.DataTypes.Float);
        store.AddColumn(UNIFIED_TABLE_NAME, "Trip_Point", OpcUa.DataTypes.String);
        store.AddColumn(UNIFIED_TABLE_NAME, "Min_or_Max", OpcUa.DataTypes.String);
        store.AddColumn(UNIFIED_TABLE_NAME, "Association_Timestamp", OpcUa.DataTypes.DateTime);
        
        // Create index on DeviceName and Local_Timestamp for better query performance
        try
        {
            store.Query($"CREATE INDEX IF NOT EXISTS idx_device_timestamp ON {UNIFIED_TABLE_NAME} (DeviceName, Local_Timestamp DESC)", out _, out _);
        }
        catch (Exception ex)
        {
            Log.Warning($"db_PQLogger-{deviceName}", $"Could not create index: {ex.Message}");
        }
    }

    private bool TableExists(Store store, string tableName)
    {
        store.Query($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'", out _, out object[,] resultSet);
        return resultSet != null && resultSet.GetLength(0) > 0 && Convert.ToInt32(resultSet[0, 0]) > 0;
    }

    private int GetEventCodeFromDictionary(string eventType) => EventDescriptions.TryGetValue(eventType, out var dict) ? dict.Keys.First() : -1;

    private string GetSubEventDescriptionFromDictionary(string eventType, int subEventCode)
    {
        if (EventDescriptions.TryGetValue(eventType, out var eventDict))
            foreach (var dict in eventDict.Values)
                if (dict.TryGetValue(subEventCode, out var desc))
                    return desc;
        return "Sub Event Code not found";
    }

    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec, string microSeconds = "0")
    {
        try
        {
            string month = monthDay.Length == 3 ? monthDay.Substring(0, 1).PadLeft(2, '0') : (int.Parse(monthDay) / 100).ToString().PadLeft(2, '0');
            string day = monthDay.Length == 3 ? monthDay.Substring(1, 2) : (int.Parse(monthDay) % 100).ToString().PadLeft(2, '0');
            string hour = (int.Parse(hourMinute) / 100).ToString().PadLeft(2, '0');
            string minute = (int.Parse(hourMinute) % 100).ToString().PadLeft(2, '0');
            string seconds = (int.Parse(secMilliSec) / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (int.Parse(secMilliSec) % 1000).ToString().PadLeft(3, '0');
            string microSecs = microSeconds.PadLeft(3, '0');
            // Return ISO 8601 format: YYYY-MM-DDTHH:MM:SS.millisecondsmicroseconds
            return $"{year}-{month}-{day}T{hour}:{minute}:{seconds}.{milliseconds}{microSecs}";
        }
        catch
        {
            return "Invalid Timestamp";
        }
    }

    public static Dictionary<string, Dictionary<int, Dictionary<int, string>>> EventDescriptions =
        new Dictionary<string, Dictionary<int, Dictionary<int, string>>>
    {
        // Voltage_Swell
        {
            "Voltage_Swell", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    1, new Dictionary<int, string>
                    {
                        { 1, "Voltage Swell (4 trip points for V1)" },
                        { 2, "Voltage Swell (4 trip points for V2)" },
                        { 3, "Voltage Swell (4 trip points for V3)" }
                    }
                }
            }
        },
        // Voltage_Sag
        {
            "Voltage_Sag", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    2, new Dictionary<int, string>
                    {
                        { 1, "Voltage Sag (5 trip points for V1)" },
                        { 2, "Voltage Sag (5 trip points for V2)" },
                        { 3, "Voltage Sag (5 trip points for V3)" }
                    }
                }
            }
        },
        // Imbalance
        {
            "Imbalance", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    3, new Dictionary<int, string>
                    {
                        { 1, "Voltage Imbalance" },
                        { 2, "Current Imbalance" }
                    }
                }
            }
        },
        // Power_Frequency
        {
            "Power_Frequency", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    4, new Dictionary<int, string>
                    {
                        { 0, "Power Frequency Deviation" }
                    }
                }
            }
        },
        // Voltage_DC_Offset
        {
            "Voltage_DC_Offset", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    5, new Dictionary<int, string>
                    {
                        { 1, "V1 DC offset" },
                        { 2, "V2 DC offset" },
                        { 3, "V3 DC offset" }
                    }
                }
            }
        },
        // Voltage THD
        {
            "Voltage THD", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    6, new Dictionary<int, string>
                    {
                        { 1, "V1 THD" },
                        { 2, "V2 THD" },
                        { 3, "V3 THD" }
                    }
                }
            }
        },
        // Current THD
        {
            "Current_THD", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    7, new Dictionary<int, string>
                    {
                        { 1, "I1 THD" },
                        { 2, "I2 THD" },
                        { 3, "I3 THD" }
                    }
                }
            }
        },
        // IEEE1159_Over_Voltage
        {
            "IEEE1159_Over_Voltage", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    8, new Dictionary<int, string>
                    {
                        { 1, "V1 over voltage" },
                        { 2, "V2 over voltage" },
                        { 3, "V3 over voltage" }
                    }
                }
            }
        },
        // IEEE1159_Under_Voltage
        {
            "IEEE1159_Under_Voltage", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    9, new Dictionary<int, string>
                    {
                        { 1, "V1 under voltage" },
                        { 2, "V2 under voltage" },
                        { 3, "V3 under voltage" }
                    }
                }
            }
        },
        // Voltage_TID
        {
            "Voltage_TID", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    10, new Dictionary<int, string>
                    {
                        { 1, "Voltage V1 total interharmonic distortion" },
                        { 2, "Voltage V2 total interharmonic distortion" },
                        { 3, "Voltage V3 total interharmonic distortion" }
                    }
                }
            }
        },
        // Current_TID
        {
            "Current_TID", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    11, new Dictionary<int, string>
                    {
                        { 1, "Current I1 total interharmonic distortion" },
                        { 2, "Current I2 total interharmonic distortion" },
                        { 3, "Current I3 total interharmonic distortion" },
                        { 4, "Current I4 total interharmonic distortion" }
                    }
                }
            }
        },
        // IEEE1159_Voltage_Fluctuations
        {
            "IEEE1159_Voltage_Fluctuations", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    12, new Dictionary<int, string>
                    {
                        { 1, "V1 Pst configured limit has been exceeded" },
                        { 2, "V2 Pst configured limit has been exceeded" },
                        { 3, "V3 Pst configured limit has been exceeded" }
                    }
                }
            }
        },
        // Voltage_Transient
        {
            "Voltage_Transient", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    13, new Dictionary<int, string>
                    {
                        { 1, "V1 transient" },
                        { 2, "V2 transient" },
                        { 3, "V3 transient" }
                    }
                }
            }
        },
        // Command_Trigger
        {
            "Command_Trigger", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    14, new Dictionary<int, string>
                    {
                        { 0, "Event triggered by the user command" }
                    }
                }
            }
        },
        // WSB_Sag
        {
            "WSB_Sag", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    15, new Dictionary<int, string>
                    {
                        { 0, "Sag event from WSB (waveform synchronization broadcast) message" }
                    }
                }
            }
        },
        // WSB_Swell
        {
            "WSB_Swell", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    16, new Dictionary<int, string>
                    {
                        { 0, "Swell event from WSB message" }
                    }
                }
            }
        },
        // WSB_Transient
        {
            "WSB_Transient", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    17, new Dictionary<int, string>
                    {
                        { 0, "Transient event from WSB message" }
                    }
                }
            }
        },
        // WSB_Command
        {
            "WSB_Command", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    18, new Dictionary<int, string>
                    {
                        { 0, "User command from WSB message" }
                    }
                }
            }
        },
        // IEEE1159_Swell
        {
            "IEEE1159_Swell", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    19, new Dictionary<int, string>
                    {
                        { 1, "V1 Voltage Swell greater than 110% of nominal" },
                        { 2, "V2 Voltage Swell greater than 110% of nominal" },
                        { 3, "V3 Voltage Swell greater than 110% of nominal" }
                    }
                }
            }
        },
        // IEEE1159_Sag
        {
            "IEEE1159_Sag", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    20, new Dictionary<int, string>
                    {
                        { 1, "V1 Voltage Sag less than 90% of nominal" },
                        { 2, "V2 Voltage Sag less than 90% of nominal" },
                        { 3, "V3 Voltage Sag less than 90% of nominal" }
                    }
                }
            }
        },
        // IEEE1159_Interruption
        {
            "IEEE1159_Interruption", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    21, new Dictionary<int, string>
                    {
                        { 1, "Voltage Interruption less than 10% nominal" },
                        { 2, "Voltage Interruption less than 10% nominal" },
                        { 3, "Voltage Interruption less than 10% nominal" }
                    }
                }
            }
        },
        // EN61000_4_30_Mains_Signaling
        {
            "EN61000_4_30_Mains_Signaling", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    22, new Dictionary<int, string>
                    {
                        { 1, "V1 mains signaling has exceeded the configured limit" },
                        { 2, "V2 mains signaling has exceeded the configured limit" },
                        { 3, "V3 mains signaling has exceeded the configured limit" }
                    }
                }
            }
        },
        // EN61000_4_30_Under_Deviation
        {
            "EN61000_4_30_Under_Deviation", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    23, new Dictionary<int, string>
                    {
                        { 1, "An under deviation is detected on V1" },
                        { 2, "An under deviation is detected on V2" },
                        { 3, "An under deviation is detected on V3" }
                    }
                }
            }
        },
        // EN61000_4_30_Over_Deviation
        {
            "EN61000_4_30_Over_Deviation", new Dictionary<int, Dictionary<int, string>>
            {
                {
                    24, new Dictionary<int, string>
                    {
                        { 1, "An over deviation is detected on V1" },
                        { 2, "An over deviation is detected on V2" },
                        { 3, "An over deviation is detected on V3" }
                    }
                }
            }
        }
    };

    /// <summary>
    /// Drops data for the current device from the unified table
    /// </summary>
    [ExportMethod]
    public void ClearDeviceData()
    {
        try
        {
            logLabel.Value = $"Clearing event data for {deviceName}...";
            var myStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            
            if (TableExists(myStore, UNIFIED_TABLE_NAME))
            {
                // Delete only records for this device
                myStore.Query($"DELETE FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{deviceName}'", out _, out _);
                logLabel.Value = $"Event data cleared for {deviceName}.";
            }
            else
            {
                logLabel.Value = $"No event data found to clear for {deviceName}.";
            }
        }
        catch (Exception ex)
        {
            logLabel.Value = $"Error clearing event data: {ex.Message}";
            Log.Error($"db_PQLogger-{deviceName}", $"Error clearing data for device '{deviceName}': {ex.Message}");
        }
    }

    
    [ExportMethod]
    public void DropUnifiedTable()
    {
        var myStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
        myStore.RemoveTable(UNIFIED_TABLE_NAME);
    }

  
    [ExportMethod]
    public int GetDeviceEventCount()
    {
        try
        {
            var myStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            
            if (TableExists(myStore, UNIFIED_TABLE_NAME))
            {
                myStore.Query($"SELECT COUNT(*) FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{deviceName}'", out _, out object[,] resultSet);
                return resultSet != null && resultSet.GetLength(0) > 0 ? Convert.ToInt32(resultSet[0, 0]) : 0;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"db_PQLogger-{deviceName}", $"Error getting event count: {ex.Message}");
            return 0;
        }
    }

}

public class PowerQualityData
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
