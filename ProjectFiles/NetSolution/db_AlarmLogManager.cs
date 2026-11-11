#region Using directives
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FluentFTP;
using System.Net.Http;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SQLiteStore;
using FTOptix.SerialPort;
#endregion

public class db_AlarmLogManager : BaseNetLogic
{
    private Store alarmStore;
    private PeriodicTask periodicAlarmTask;
    private const string TABLE_NAME = "AlarmLog_AllDevices";
    private string deviceName;
    private string ipAddress;
    private HttpClient httpClient;
    private IUAVariable enableAlarmLogging;
    private DateTime lastDeviceReadyCheck = DateTime.MinValue;
    private bool lastDeviceReadyStatus = false;

    private readonly Dictionary<int, (string AlarmTypeDescription, Dictionary<int, string> AlarmCodes)> alarmDictionary = new()
    {
        { 1, ("Metering Status", new Dictionary<int, string> { { 1, "Virtual Wiring Correction" }, { 2, "Volts Loss V1" }, { 4, "Volts Loss V2" }, { 8, "Volts Loss V3" }, { 16, "Voltage Over Range Indication" }, { 32, "Ampere Over Range Indication" }, { 64, "Wiring Diagnostics Active" } }) },
        { 2, ("Over Range Information", new Dictionary<int, string> { { 1, "V1G Over Range" }, { 2, "V2G Over Range" }, { 4, "V3G Over Range" }, { 8, "VNG Over Range" }, { 16, "I1 Over Range" }, { 32, "I2 Over Range" }, { 64, "I3 Over Range" }, { 128, "I4 Over Range" } }) },
        { 4, ("Power Quality Status", new Dictionary<int, string> { { 1, "Sag Indication Detected" }, { 2, "Swell Indication Detected" }, { 4, "Transient Indication" }, { 8, "200mS Sag Swell Status Flag" }, { 16, "3s Sag Swell Status Flag" }, { 32, "10m Sag Swell Status Flag" }, { 64, "2h Sag Swell Status Flag" } }) },
        { 8, ("Logs Status", new Dictionary<int, string> { { 1, "Data Log Full Fill And Stop" }, { 2, "Event Log Full Fill And Stop" }, { 4, "Setpoint Log Full Fill And Stop" }, { 8, "PowerDuality Log Full Fill And Stop" }, { 16, "Energy Log Full Fill And Stop" }, { 32, "Waveform Full" }, { 64, "TriggerData Full Fill And Stop" } }) },
        { 16, ("Output Pulse Overrun", new Dictionary<int, string> { { 1, "KYZ Pulse Overrun" }, { 2, "Relay1 Pulse Overrun" }, { 4, "Relay2 Pulse Overrun" }, { 8, "Relay3 Pulse Overrun" } }) },
        { 32, ("IEEE1159 Over/UnderVoltage Imbalance", new Dictionary<int, string> { { 1, "IEEE1159 Over Voltage V1" }, { 2, "IEEE1159 Over Voltage V2" }, { 4, "IEEE1159 Over Voltage V3" }, { 8, "IEEE1159 Under Voltage V1" }, { 16, "IEEE1159 Under Voltage V2" }, { 32, "IEEE1159 Under Voltage V3" }, { 64, "IEEE1159 Imbalance Condition Volts" }, { 128, "IEEE1159 Imbalance Condition Current" } }) },
        { 64, ("IEEE1159 DCOffset THD Frequency Condition", new Dictionary<int, string> { { 1, "IEEE1159 DCOffset Condition V1" }, { 2, "IEEE1159 DCOffset Condition V2" }, { 4, "IEEE1159 DCOffset Condition V3" }, { 8, "IEEE1159 Voltage THD Condition V1" }, { 16, "IEEE1159 Voltage THD Condition V2" }, { 32, "IEEE1159 Voltage THD Condition V3" }, { 64, "IEEE1159 Current THD Condition I1" }, { 128, "IEEE1159 Current THD Condition I2" }, { 256, "IEEE1159 Current THD Condition I3" }, { 512, "IEEE1159 PowerFrequency Condition" }, { 1024, "IEEE1159 Current THD Condition I4" } }) },
        { 65, ("IEEE1159 TID Condition", new Dictionary<int, string> { { 1, "IEEE1159 Voltage TID Condition V1" }, { 2, "IEEE1159 Voltage TID Condition V2" }, { 4, "IEEE1159 Voltage TID Condition V3" }, { 8, "IEEE1159 Current TID Condition I1" }, { 16, "IEEE1159 Current TID Condition I2" }, { 32, "IEEE1159 Current TID Condition I3" }, { 64, "IEEE1159 Current TID Condition I4" } }) },
        { 128, ("IEEE1159 Overall Status", new Dictionary<int, string> { { 1, "ShortTerm TDD THD PASS FAIL" }, { 2, "LongTerm TDD THD PASS FAIL" }, { 4, "ShortTerm Individual Harmonic PASS FAIL" }, { 8, "LongTerm Individual Harmonic PASS FAIL" } }) },
        { 256, ("ShortTerm 2nd To 17th Harmonic Status", new Dictionary<int, string> { { 1, "2nd Harmonic PASS FAIL" }, { 2, "3rd Harmonic PASS FAIL" }, { 4, "4th Harmonic PASS FAIL" }, { 8, "5th Harmonic PASS FAIL" }, { 16, "6th Harmonic PASS FAIL" }, { 32, "7th Harmonic PASS FAIL" }, { 64, "8th Harmonic PASS FAIL" }, { 128, "9th Harmonic PASS FAIL" }, { 256, "10th Harmonic PASS FAIL" }, { 512, "11th Harmonic PASS FAIL" }, { 1024, "12th Harmonic PASS FAIL" }, { 2048, "13th Harmonic PASS FAIL" }, { 4096, "14th Harmonic PASS FAIL" }, { 8192, "15th Harmonic PASS FAIL" }, { 16384, "16th Harmonic PASS FAIL" }, { 32768, "17th Harmonic PASS FAIL" } }) },
        { 512, ("ShortTerm 18th To 33rd Harmonic Status", new Dictionary<int, string> { { 1, "18th Harmonic PASS FAIL" }, { 2, "19th Harmonic PASS FAIL" }, { 4, "20th Harmonic PASS FAIL" }, { 8, "21st Harmonic PASS FAIL" }, { 16, "22nd Harmonic PASS FAIL" }, { 32, "23rd Harmonic PASS FAIL" }, { 64, "24th Harmonic PASS FAIL" }, { 128, "25th Harmonic PASS FAIL" }, { 256, "26th Harmonic PASS FAIL" }, { 512, "27th Harmonic PASS FAIL" }, { 1024, "28th Harmonic PASS FAIL" }, { 2048, "29th Harmonic PASS FAIL" }, { 4096, "30th Harmonic PASS FAIL" }, { 8192, "31st Harmonic PASS FAIL" }, { 16384, "32nd Harmonic PASS FAIL" }, { 32768, "33rd Harmonic PASS FAIL" } }) },
        { 1024, ("ShortTerm 34th To 40th Harmonic Status", new Dictionary<int, string> { { 1, "34th Harmonic PASS FAIL" }, { 2, "35th Harmonic PASS FAIL" }, { 4, "36th Harmonic PASS FAIL" }, { 8, "37th Harmonic PASS FAIL" }, { 16, "38th Harmonic PASS FAIL" }, { 32, "39th Harmonic PASS FAIL" }, { 64, "40th Harmonic PASS FAIL" } }) },
        { 2048, ("LongTerm 2nd To 17th Harmonic Status", new Dictionary<int, string> { { 1, "2nd Harmonic PASS FAIL" }, { 2, "3rd Harmonic PASS FAIL" }, { 4, "4th Harmonic PASS FAIL" }, { 8, "5th Harmonic PASS FAIL" }, { 16, "6th Harmonic PASS FAIL" }, { 32, "7th Harmonic PASS FAIL" }, { 64, "8th Harmonic PASS FAIL" }, { 128, "9th Harmonic PASS FAIL" }, { 256, "10th Harmonic PASS FAIL" }, { 512, "11th Harmonic PASS FAIL" }, { 1024, "12th Harmonic PASS FAIL" }, { 2048, "13th Harmonic PASS FAIL" }, { 4096, "14th Harmonic PASS FAIL" }, { 8192, "15th Harmonic PASS FAIL" }, { 16384, "16th Harmonic PASS FAIL" }, { 32768, "17th Harmonic PASS FAIL" } }) },
        { 4096, ("LongTerm 18th To 33rd Harmonic Status", new Dictionary<int, string> { { 1, "18th Harmonic PASS FAIL" }, { 2, "19th Harmonic PASS FAIL" }, { 4, "20th Harmonic PASS FAIL" }, { 8, "21st Harmonic PASS FAIL" }, { 16, "22nd Harmonic PASS FAIL" }, { 32, "23rd Harmonic PASS FAIL" }, { 64, "24th Harmonic PASS FAIL" }, { 128, "25th Harmonic PASS FAIL" }, { 256, "26th Harmonic PASS FAIL" }, { 512, "27th Harmonic PASS FAIL" }, { 1024, "28th Harmonic PASS FAIL" }, { 2048, "29th Harmonic PASS FAIL" }, { 4096, "30th Harmonic PASS FAIL" }, { 8192, "31st Harmonic PASS FAIL" }, { 16384, "32nd Harmonic PASS FAIL" }, { 32768, "33rd Harmonic PASS FAIL" } }) },
        { 8192, ("LongTerm 34th To 40th Harmonic Status", new Dictionary<int, string> { { 1, "34th Harmonic PASS FAIL" }, { 2, "35th Harmonic PASS FAIL" }, { 4, "36th Harmonic PASS FAIL" }, { 8, "37th Harmonic PASS FAIL" }, { 16, "38th Harmonic PASS FAIL" }, { 32, "39th Harmonic PASS FAIL" }, { 64, "40th Harmonic PASS FAIL" } }) },
        { 16384, ("IEEE1159 Voltage Fluctuation Condition", new Dictionary<int, string> { { 1, "IEEE1159 Voltage Fluctuation Condition V1" }, { 2, "IEEE1159 Voltage Fluctuation Condition V2" }, { 4, "IEEE1159 Voltage Fluctuation Condition V3" } }) },
        { 32768, ("EN61000 4 30 Plains Signal Condition", new Dictionary<int, string> { { 1, "EN61000 4 30 Mains Signal Condition V1" }, { 2, "EN61000 4 30 Mains Signal Condition V2" }, { 4, "EN61000 4 30 Mains Signal Condition V3" }, { 8, "EN61000 4 30 Under Deviation V1" }, { 16, "EN61000 4 30 Under Deviation V2" }, { 32, "EN61000 4 30 Under Deviation V3" }, { 64, "EN61000 4 30 Over Deviation V1" }, { 128, "EN61000 4 30 Over Deviation V2" }, { 256, "EN61000 4 30 Over Deviation V3" } }) }
    };

    public db_AlarmLogManager()
    {
        // Initialize HttpClient for HTTP downloads
        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(300); // 5 minute timeout
    }

    public override void Start()
    {
        alarmStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
            .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

        if (alarmStore == null)
        {
            Log.Error("AlarmLogManager", "PowerQuality DB not found");
            return;
        }

        deviceName = Owner.BrowseName;
        ipAddress = Owner.GetVariable("Val_IPAddress")?.Value;
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            Log.Error($"AlarmLogManager-{deviceName}", "IP address missing");
            return;
        }

        CreateTableIfNotExists();

        int checkPeriodSeconds = ReadDurationVariable("Cfg_AlarmLogCheckPeriodSeconds", 600); // Default 10 min

        // Read enable/disable logging variable
        enableAlarmLogging = Owner.GetVariable("Set_EnableAlarmLogLogging");
        if (enableAlarmLogging == null)
        {
            Log.Warning($"AlarmLogManager-{deviceName}", "Set_EnableAlarmLogLogging variable not found, defaulting to enabled");
            enableAlarmLogging = InformationModel.MakeVariable("Set_EnableAlarmLogLogging", OpcUa.DataTypes.Boolean);
            enableAlarmLogging.Value = true;
            Owner.Add(enableAlarmLogging);
        }

        if (enableAlarmLogging.Value)
        {
            // Always start periodic task regardless of device ready status
            // The ready check happens inside DownloadAndStoreAlarmLog to allow retries when device comes online
            periodicAlarmTask = new PeriodicTask(DownloadAndStoreAlarmLog, checkPeriodSeconds * 1000, LogicObject);
            periodicAlarmTask.Start();
            Log.Info($"AlarmLogManager-{deviceName}", $"Started periodic alarm log download every {checkPeriodSeconds} seconds");
        }
        else
        {
            Log.Info($"AlarmLogManager-{deviceName}", "Alarm logging is disabled (Set_EnableAlarmLogLogging = False)");
        }
    }

    public override void Stop()
    {
        periodicAlarmTask?.Cancel();
        periodicAlarmTask?.Dispose();
        periodicAlarmTask = null;
        enableAlarmLogging.VariableChange -= EnableAlarmLogging_VariableChange;
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
                if (isReady)
                    Log.Info($"AlarmLogManager_HTTP-{deviceName}", "Device is now Ready");
                else
                    Log.Warning($"AlarmLogManager_HTTP-{deviceName}", "Device is Not Ready (HTTP check failed)");

                lastDeviceReadyStatus = isReady;
            }

            lastDeviceReadyCheck = DateTime.Now;
            return isReady;
        }
        catch (Exception ex)
        {
            // Log transitions only
            if (lastDeviceReadyStatus)
            {
                Log.Warning($"AlarmLogManager_HTTP-{deviceName}", $"Device communication failed: {ex.Message}");
                lastDeviceReadyStatus = false;
            }

            lastDeviceReadyCheck = DateTime.Now;
            return false;
        }
    }

    private void EnableAlarmLogging_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            if (periodicAlarmTask == null)
            {
                int checkPeriodSeconds = ReadDurationVariable("Cfg_AlarmLogCheckPeriodSeconds", 600);
                periodicAlarmTask = new PeriodicTask(DownloadAndStoreAlarmLog, checkPeriodSeconds * 1000, LogicObject);
                periodicAlarmTask.Start();
                Log.Info($"AlarmLogManager-{deviceName}", "Alarm logging enabled - started periodic download");
            }
        }
        else
        {
            if (periodicAlarmTask != null)
            {
                periodicAlarmTask.Cancel();
                periodicAlarmTask.Dispose();
                periodicAlarmTask = null;
                Log.Info($"AlarmLogManager-{deviceName}", "Alarm logging disabled - stopped periodic download");
            }
        }
    }

    private void CreateTableIfNotExists()
    {
        try
        {
            if (!TableExists(alarmStore, TABLE_NAME))
            {
                alarmStore.AddTable(TABLE_NAME);
                
                // Columns matching CSV data + calculated fields
                alarmStore.AddColumn(TABLE_NAME,"DeviceName", OpcUa.DataTypes.String);
                alarmStore.AddColumn(TABLE_NAME,"Timestamp", OpcUa.DataTypes.DateTime);
                alarmStore.AddColumn(TABLE_NAME,"AlarmTypeCode", OpcUa.DataTypes.Int32);
                alarmStore.AddColumn(TABLE_NAME,"AlarmTypeDescription", OpcUa.DataTypes.String);
                alarmStore.AddColumn(TABLE_NAME,"AlarmCode", OpcUa.DataTypes.Int32);
                alarmStore.AddColumn(TABLE_NAME,"AlarmCodeDescription", OpcUa.DataTypes.String);
                alarmStore.AddColumn(TABLE_NAME,"Severity", OpcUa.DataTypes.String); // Calculated field
                alarmStore.AddColumn(TABLE_NAME,"CreatedAt", OpcUa.DataTypes.DateTime);
                
                // Create indexes for performance
                try
                {
                    alarmStore.Query($"CREATE INDEX IF NOT EXISTS idx_device_time ON {TABLE_NAME}(DeviceName, Timestamp DESC)", out _, out _);
                    alarmStore.Query($"CREATE INDEX IF NOT EXISTS idx_severity ON {TABLE_NAME}(Severity)", out _, out _);
                    alarmStore.Query($"CREATE INDEX IF NOT EXISTS idx_timestamp ON {TABLE_NAME}(Timestamp DESC)", out _, out _);
                    Log.Info($"AlarmLogManager-{deviceName}", "Indexes created successfully");
                }
                catch (Exception ex)
                {
                    Log.Warning($"AlarmLogManager-{deviceName}", $"Index creation warning: {ex.Message}");
                }
                
                Log.Info($"AlarmLogManager-{deviceName}", $"Table {TABLE_NAME} created successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"Failed to create table: {ex.Message}");
        }
    }

    private bool TableExists(Store store, string tableName)
    {
        try
        {
            var query = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            store.Query(query, out _, out object[,] resultSet);
            return resultSet != null && resultSet.GetLength(0) > 0 && Convert.ToInt32(resultSet[0, 0]) > 0;
        }
        catch { return false; }
    }

    private DateTime GetLastProcessedTimestamp()
    {
        try
        {
            string query = $"SELECT MAX(Timestamp) FROM {TABLE_NAME} WHERE DeviceName = '{deviceName}'";
            alarmStore.Query(query, out _, out object[,] resultSet);
            if (resultSet != null && resultSet.GetLength(0) > 0 && resultSet[0, 0] != null && resultSet[0, 0] != DBNull.Value)
                return DateTime.Parse(resultSet[0, 0].ToString());
        }
        catch (Exception ex)
        {
            Log.Warning($"AlarmLogManager-{deviceName}", $"Could not get last timestamp: {ex.Message}");
        }
        return DateTime.MinValue; // No records yet
    }

    private void DownloadAndStoreAlarmLog()
    {
        try
        {
            // Check if alarm logging is enabled
            if (enableAlarmLogging == null || !enableAlarmLogging.Value)
            {
                return;
            }

            // Check device readiness (independent check each cycle)
            if (!IsDeviceReady())
            {
                return; // Skip this cycle, will check again next time
            }
            
            // Use HTTP client (lightweight, frequent downloads)
            string url = $"http://{ipAddress}/LoggingResults/Alarm_Log.csv";
            
            using (var ms = new MemoryStream())
            {
                byte[] fileBytes = httpClient.GetByteArrayAsync(url).Result;
                ms.Write(fileBytes, 0, fileBytes.Length);
                ms.Position = 0;
                
                var lines = new StreamReader(ms).ReadToEnd().Split('\n');
                
                int newAlarmsCount = 0;
                int skippedAlarmsCount = 0;
                
                foreach (var line in lines.Skip(1)) // Skip header
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    var row = line.Split(',');
                    if (row.Length < 7) continue;
                    
                    string timestampStr = FormatTimestamp(row[1], row[2], row[3], row[4]);
                    DateTime alarmTime = DateTime.Parse(timestampStr);
                    DateTime lastProcessed = GetLastProcessedTimestamp();

                    if (alarmTime <= lastProcessed)
                    {
                        skippedAlarmsCount++;
                        continue;
                    }

                    InsertAlarmRecord(row, alarmTime);
                    newAlarmsCount++;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"[PERIODIC] HTTP error downloading alarm log: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"[PERIODIC] Failed to download alarm log: {ex.Message}");
        }
    }

    private void InsertAlarmRecord(string[] row, DateTime alarmTime)
    {
        try
        {
            int alarmTypeCode = int.Parse(row[5]);
            int alarmCodeSum = int.Parse(row[6]);

            var alarmTypeDesc = GetAlarmTypeDescription(alarmTypeCode);
            var alarmCodes = DecodeAlarmCodes(alarmTypeCode, alarmCodeSum);

            foreach (var code in alarmCodes)
            {
                string severity = DetermineAlarmSeverity(alarmTypeCode, code.Key);
                
                string[] columns = { 
                    "DeviceName", "Timestamp", "AlarmTypeCode", "AlarmTypeDescription", 
                    "AlarmCode", "AlarmCodeDescription", "Severity", "CreatedAt" 
                };
                
                object[,] values = new object[1, columns.Length];
                values[0, 0] = deviceName;
                values[0, 1] = alarmTime;
                values[0, 2] = alarmTypeCode;
                values[0, 3] = alarmTypeDesc;
                values[0, 4] = code.Key;
                values[0, 5] = code.Value;
                values[0, 6] = severity;
                values[0, 7] = DateTime.Now;

                alarmStore.Tables.Get<Table>(TABLE_NAME).Insert(columns, values);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"Insert failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Determine alarm severity based on type and code
    /// </summary>
    private string DetermineAlarmSeverity(int alarmTypeCode, int alarmCode)
    {
        // Critical: Voltage Loss, Over Range, Communication Loss
        if (alarmTypeCode == 1) // Voltage Loss
            return "Critical";
            
        if (alarmTypeCode == 2) // Over Range Information
            return "Critical";

        if (alarmTypeCode == 32 && (alarmCode == 1 || alarmCode == 2 || alarmCode == 4 || alarmCode == 8 || alarmCode == 16 || alarmCode == 32)) // IEEE1159_Over/Under_Voltage
            return "Critical";
        
        if (alarmTypeCode == 64 && (alarmCode == 1 || alarmCode == 2 || alarmCode == 4 )) // IEEE1159_DCOffset_Condition
            return "Critical";
        
        if (alarmTypeCode == 128) // IEEE519_Overall_Status
            return "Critical";

        // Warning: Power Quality Issues, IEEE1159 Violations
        if (alarmTypeCode == 4) // Power Quality Status
            return "Warning";
        
        if (alarmTypeCode == 64 && (alarmCode == 8 || alarmCode == 16 || alarmCode == 32 || alarmCode == 64 || alarmCode == 128 || alarmCode == 256 || alarmCode == 512 || alarmCode == 1024)) // IEEE1159_Voltage/Current_THD_Condition
            return "Warning";
            
        if (alarmTypeCode == 32 || alarmTypeCode == 64 || alarmTypeCode == 128) // IEEE1159 conditions
            return "Warning";
            
        if (alarmTypeCode == 16384) // Voltage Fluctuation
            return "Warning";

        if (alarmTypeCode == 256) 
            return "Warning";
        
        if (alarmTypeCode == 512) 
            return "Warning";

        if (alarmTypeCode == 1024) 
            return "Warning";

        if (alarmTypeCode == 2048) 
            return "Warning";

        if (alarmTypeCode == 4096)
            return "Warning";
        
        if (alarmTypeCode == 8192)
            return "Warning";
        if (alarmTypeCode == 32768) 
            return "Warning";
            
        // Info: Everything else (logs, harmonics, etc.)
        return "Info";
    }

    private Dictionary<int, string> DecodeAlarmCodes(int typeCode, int codeSum)
    {
        var result = new Dictionary<int, string>();
        if (alarmDictionary.TryGetValue(typeCode, out var info))
        {
            foreach (var kvp in info.AlarmCodes)
            {
                if ((codeSum & kvp.Key) == kvp.Key)
                    result.Add(kvp.Key, kvp.Value);
            }
        }
        return result;
    }

    private string GetAlarmTypeDescription(int alarmTypeCode)
    {
        return alarmDictionary.TryGetValue(alarmTypeCode, out var info) 
            ? info.AlarmTypeDescription 
            : $"Unknown Type {alarmTypeCode}";
    }

    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            int monthDayInt = int.Parse(monthDay);
            string month = (monthDayInt / 100).ToString().PadLeft(2, '0');
            string day = (monthDayInt % 100).ToString().PadLeft(2, '0');
            
            int hourMinInt = int.Parse(hourMinute);
            string hour = (hourMinInt / 100).ToString().PadLeft(2, '0');
            string minute = (hourMinInt % 100).ToString().PadLeft(2, '0');
            
            int secMsInt = int.Parse(secMilliSec);
            string seconds = (secMsInt / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (secMsInt % 1000).ToString().PadLeft(3, '0');
            
            return $"{year}-{month}-{day} {hour}:{minute}:{seconds}.{milliseconds}";
        }
        catch 
        { 
            return "Invalid Timestamp"; 
        }
    }

    private int ReadDurationVariable(string variableName, int defaultValueSeconds)
    {
        try
        {
            var variable = Owner.GetVariable(variableName);
            if (variable != null)
            {
                int durationSeconds = variable.Value;
                return durationSeconds > 0 ? durationSeconds : defaultValueSeconds;
            }
            return defaultValueSeconds;
        }
        catch { return defaultValueSeconds; }
    }

    // ═══════════════════════════════════════════════════════════════
    // DASHBOARD QUERY METHODS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Get alarm counts grouped by severity for dashboard KPIs
    /// </summary>
    [ExportMethod]
    public void GetAlarmCountsBySeverity(out int critical, out int warning, out int info)
    {
        critical = 0;
        warning = 0;
        info = 0;
        
        try
        {
            string query = $"SELECT Severity, COUNT(*) as Count FROM {TABLE_NAME} " +
                          $"WHERE DeviceName = '{deviceName}' " +
                          $"GROUP BY Severity";
            
            alarmStore.Query(query, out _, out object[,] results);
            
            if (results != null && results.GetLength(0) > 0)
            {
                for (int i = 0; i < results.GetLength(0); i++)
                {
                    string severity = results[i, 0]?.ToString();
                    int count = Convert.ToInt32(results[i, 1]);
                    
                    switch (severity)
                    {
                        case "Critical": critical = count; break;
                        case "Warning": warning = count; break;
                        case "Info": info = count; break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"GetAlarmCountsBySeverity failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Get recent alarms for dashboard display (last N alarms)
    /// </summary>
    [ExportMethod]
    public object[,] GetRecentAlarms(int limit)
    {
        try
        {
            string query = $"SELECT Timestamp, AlarmCodeDescription, Severity " +
                          $"FROM {TABLE_NAME} " +
                          $"WHERE DeviceName = '{deviceName}' " +
                          $"ORDER BY Timestamp DESC " +
                          $"LIMIT {limit}";
            
            alarmStore.Query(query, out _, out object[,] results);
            return results ?? new object[0, 0];
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"GetRecentAlarms failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Get alarm trend by hour for chart (last N hours)
    /// </summary>
    [ExportMethod]
    public object[,] GetAlarmTrendByHour(int hoursBack)
    {
        try
        {
            string query = $"SELECT strftime('%Y-%m-%d %H:00:00', Timestamp) as Hour, " +
                          $"COUNT(*) as Count " +
                          $"FROM {TABLE_NAME} " +
                          $"WHERE DeviceName = '{deviceName}' " +
                          $"AND Timestamp >= datetime('now', '-{hoursBack} hours') " +
                          $"GROUP BY Hour " +
                          $"ORDER BY Hour";
            
            alarmStore.Query(query, out _, out object[,] results);
            return results ?? new object[0, 0];
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"GetAlarmTrendByHour failed: {ex.Message}");
            return new object[0, 0];
        }
    }

    /// <summary>
    /// Get total alarm count for a device
    /// </summary>
    [ExportMethod]
    public int GetTotalAlarmCount()
    {
        try
        {
            string query = $"SELECT COUNT(*) FROM {TABLE_NAME} WHERE DeviceName = '{deviceName}'";
            alarmStore.Query(query, out _, out object[,] results);
            
            if (results != null && results.GetLength(0) > 0)
                return Convert.ToInt32(results[0, 0]);
        }
        catch (Exception ex)
        {
            Log.Error($"AlarmLogManager-{deviceName}", $"GetTotalAlarmCount failed: {ex.Message}");
        }
        return 0;
    }
}
