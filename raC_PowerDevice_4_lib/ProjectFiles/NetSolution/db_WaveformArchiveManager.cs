#region Using directives

using System;
using System.IO;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Core;
using System.Net.Http;
using System.Collections.Generic;
using FluentFTP;
using System.Net;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;

#endregion

public class db_WaveformArchiveManager : BaseNetLogic
{
    private Store waveformStore;
    private string archiveBasePath;
    private long maxArchiveSizeBytes;
    private PeriodicTask periodicDownloadTask;
    private const string TABLE_NAME = "WaveformArchive";
    private bool enableWaveformCheckAndStore;
    private IUAVariable enableWaveformCheckAndStoreVar;
    private IUAVariable statusPeriodic;
    private IUAVariable statusLastUpdate;
    private DateTime lastDeviceReadyCheck = DateTime.MinValue;
    private bool lastDeviceReadyStatus = false;

    public override void Start()
    {
        // Get reference to embedded database
        waveformStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_WaveformArchive");
        if (waveformStore == null)
        {
            Log.Error("db_WaveformArchive", "WaveformArchiveDB not found");
            return;
        }

        // Get status variables
        statusPeriodic = LogicObject.GetVariable("Val_WaveformArchive_PeriodicStatus");
        if (statusPeriodic == null)
        {
            statusPeriodic = InformationModel.MakeVariable("Val_WaveformArchive_PeriodicStatus", OpcUa.DataTypes.String);
            //statusPeriodic.Value = "Periodic: Waiting for init...";
            Owner.Add(statusPeriodic);
        }
        statusLastUpdate = LogicObject.GetVariable("Val_WaveformArchive_LastUpdate");
        if (statusLastUpdate == null)
        {
            statusLastUpdate = InformationModel.MakeVariable("Val_WaveformArchive_LastUpdate", OpcUa.DataTypes.String);
            //statusLastUpdate.Value = "Last Update: Starting...";
            Owner.Add(statusLastUpdate);
        }

        // Read user-configurable parameters from Owner object
        enableWaveformCheckAndStoreVar = Owner.GetVariable("Set_EnableWaveformCheckAndStore");
        if (enableWaveformCheckAndStoreVar != null)
        {
            enableWaveformCheckAndStore = enableWaveformCheckAndStoreVar.Value;
            enableWaveformCheckAndStoreVar.VariableChange += EnableWaveformCheckAndStore_VariableChange;
        }
        else
        {
            enableWaveformCheckAndStore = true;
        }
        
        if (!enableWaveformCheckAndStore)
        {
            Log.Info("db_WaveformArchive", "Waveform check and store is DISABLED by configuration");
            if (statusPeriodic != null) statusPeriodic.Value = "Waveform Archive Is Disabled";
            if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: N/A";
            return;
        }
        
        if (statusPeriodic != null) statusPeriodic.Value = "Status: Initializing...";
        if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: Starting...";

        double maxArchiveSizeGB = ReadDoubleVariable("Cfg_WaveformMaxArchiveSizeGB", 5.0); // Default 5 GB
        maxArchiveSizeBytes = (long)(maxArchiveSizeGB * 1024 * 1024 * 1024);
        Log.Info("db_WaveformArchive", $"Max archive size configured: {maxArchiveSizeGB} GB ({maxArchiveSizeBytes} bytes)");

        int checkPeriodSeconds = ReadDurationVariable("Cfg_WaveformCheckPeriodSeconds", 900); // Default 15 minutes (900 seconds)
        int checkPeriodms = checkPeriodSeconds * 1000;
        Log.Info("db_WaveformArchive", $"Waveform check period configured: {checkPeriodSeconds} seconds ({checkPeriodms} ms)");

        //DropTable();
        //Create table if it doesn't exist
        CreateTableIfNotExists();

        // ✅ NEW: Read user-configured storage path
        string configuredStoragePath = ReadStoragePath("Cfg_StoragePath", "%APPLICATIONDIR%");
        
        archiveBasePath = Path.Combine(configuredStoragePath, "WaveformArchive");
        
        // Create base directory if not exists
        try
        {
            if (!Directory.Exists(archiveBasePath))
            {
                Directory.CreateDirectory(archiveBasePath);
                Log.Info("db_WaveformArchive", $"Created archive directory: {archiveBasePath}");
            }
            
            Log.Info("db_WaveformArchive", $"Archive initialized at: {archiveBasePath}");
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to create archive directory at '{archiveBasePath}': {ex.Message}");
            Log.Warning("db_WaveformArchive", "Falling back to %APPLICATIONDIR%");
            
            // Fallback to application directory
            string fallbackUri = new ResourceUri("%APPLICATIONDIR%\\").Uri;
            archiveBasePath = Path.Combine(fallbackUri, "WaveformArchive");
            Directory.CreateDirectory(archiveBasePath);
        }
        Log.Info("db_WaveformArchive", $"Archive initialized at: {archiveBasePath}");
        PerformIntegrityCheck();

        // Start periodic download task
        periodicDownloadTask = new PeriodicTask(PeriodicDownloadFiles, checkPeriodms, LogicObject);
        periodicDownloadTask.Start();
        if (statusPeriodic != null) statusPeriodic.Value = "Status: Started successfully - waiting for first check";
        Log.Info("db_WaveformArchive", $"Periodic download task started (every {checkPeriodSeconds} seconds)");
    }

    public override void Stop()
    {
        if (enableWaveformCheckAndStoreVar != null)
        {
            enableWaveformCheckAndStoreVar.VariableChange -= EnableWaveformCheckAndStore_VariableChange;
        }
        
        if (periodicDownloadTask != null)
        {
            periodicDownloadTask.Cancel();
            periodicDownloadTask.Dispose();
            periodicDownloadTask = null;
        }
        Log.Info("db_WaveformArchive", "Archive manager stopped");
    }

    /// <summary>
    /// Check if device is ready by attempting to fetch overview page
    /// This is a direct HTTP check independent of cached DeviceStatus variables
    /// </summary>
    private bool IsDeviceReady()
    {
        try
        {
            var ipVariable = Owner.GetVariable("Val_IPAddress");
            string ip = ipVariable?.Value;
            if (string.IsNullOrWhiteSpace(ip))
                return false;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = client.GetAsync($"http://{ip}/overview.shtm").Result;
            bool isReady = response.IsSuccessStatusCode;

            // Log status transitions only (avoid spamming logs)
            if (isReady != lastDeviceReadyStatus)
            {
                if (isReady)
                    Log.Info($"db_WaveformArchive_HTTP-{Owner.BrowseName}", "Device is now Ready");
                else
                    Log.Warning($"db_WaveformArchive_HTTP-{Owner.BrowseName}", "Device is Not Ready (HTTP check failed)");

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
                Log.Warning($"db_WaveformArchive_HTTP-{Owner.BrowseName}", $"Device communication failed: {ex.Message}");
                lastDeviceReadyStatus = false;
            }

            lastDeviceReadyCheck = DateTime.Now;
            return false;
        }
    }

    private void EnableWaveformCheckAndStore_VariableChange(object sender, VariableChangeEventArgs e)
    {
        bool newValue = (bool)e.NewValue;
        
        if (newValue)
        {
            // Enabled
            if (statusPeriodic != null) statusPeriodic.Value = "Status: Waiting for next periodic check...";
            if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: Re-enabled by user";
            Log.Info("db_WaveformArchive", "Waveform archive ENABLED by user");
        }
        else
        {
            // Disabled
            if (statusPeriodic != null) statusPeriodic.Value = "Waveform Archive Is Disabled";
            if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: N/A";
            Log.Info("db_WaveformArchive", "Waveform archive DISABLED by user");
        }
    }

    #region Configuration Helper Methods

    /// <summary>
    /// Reads a boolean variable from Owner object with a default fallback
    /// </summary>
    private bool ReadBooleanVariable(string variableName, bool defaultValue)
    {
        try
        {
            var variable = Owner.GetVariable(variableName);
            if (variable != null)
            {
                return variable.Value;
            }
            Log.Warning("db_WaveformArchive", $"{variableName} not found. Using default: {defaultValue}");
            return defaultValue;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error reading {variableName}: {ex.Message}. Using default: {defaultValue}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Reads a double variable from Owner object with a default fallback
    /// </summary>
    private double ReadDoubleVariable(string variableName, double defaultValue)
    {
        try
        {
            var variable = Owner.GetVariable(variableName);
            if (variable != null)
            {
                return variable.Value;
            }
            Log.Warning("db_WaveformArchive", $"{variableName} not found. Using default: {defaultValue}");
            return defaultValue;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error reading {variableName}: {ex.Message}. Using default: {defaultValue}");
            return defaultValue;
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
            Log.Warning("db_WaveformArchive", $"{variableName} not found. Using default: {defaultValueSeconds} seconds");
            return defaultValueSeconds;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error reading {variableName}: {ex.Message}. Using default: {defaultValueSeconds} seconds");
            return defaultValueSeconds;
        }
    }


    private string ReadFTPUsername(string defaultUsername = "Admin")
    {
        try
        {
            var variable = Owner.GetVariable("Username");
            if (variable != null && variable.Value != null)
            {
                string username = variable.Value.Value.ToString();
                if (!string.IsNullOrWhiteSpace(username))
                {
                    return username;
                }
            }
            Log.Warning("db_WaveformArchive", $"FTP Username not found. Using default: {defaultUsername}");
            return defaultUsername;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error reading FTP Username: {ex.Message}. Using default: {defaultUsername}");
            return defaultUsername;
        }
    }


    private string ReadFTPPassword(string defaultPassword = "Admin")
    {
        try
        {
            var variable = Owner.GetVariable("Password");
            if (variable != null && variable.Value != null)
            {
                string password = variable.Value.Value.ToString();
                if (!string.IsNullOrWhiteSpace(password))
                {
                    return password;
                }
            }
            Log.Warning("db_WaveformArchive", $"FTP Password not found. Using default: {defaultPassword}");
            return defaultPassword;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error reading FTP Password: {ex.Message}. Using default: {defaultPassword}");
            return defaultPassword;
        }
    }

    #endregion

    private void CreateTableIfNotExists()
    {
        try
        {
            if (!TableExists(waveformStore, TABLE_NAME))
            {
                Log.Info("db_WaveformArchive", $"Creating table {TABLE_NAME}...");
                waveformStore.AddTable(TABLE_NAME);

                // Add columns with proper data types
                waveformStore.AddColumn(TABLE_NAME, "Id", OpcUa.DataTypes.Int32);
                waveformStore.AddColumn(TABLE_NAME, "DeviceName", OpcUa.DataTypes.String);
                waveformStore.AddColumn(TABLE_NAME, "FileName", OpcUa.DataTypes.String);
                waveformStore.AddColumn(TABLE_NAME, "FilePath", OpcUa.DataTypes.String);
                waveformStore.AddColumn(TABLE_NAME, "FileSize", OpcUa.DataTypes.Int32);
                waveformStore.AddColumn(TABLE_NAME, "CaptureDateTime", OpcUa.DataTypes.DateTime);
                waveformStore.AddColumn(TABLE_NAME, "TotalCycles", OpcUa.DataTypes.Int32);
                waveformStore.AddColumn(TABLE_NAME, "MaxVoltage", OpcUa.DataTypes.Double);
                waveformStore.AddColumn(TABLE_NAME, "MaxCurrent", OpcUa.DataTypes.Double);
                waveformStore.AddColumn(TABLE_NAME, "EventType", OpcUa.DataTypes.String);
                waveformStore.AddColumn(TABLE_NAME, "CreatedAt", OpcUa.DataTypes.DateTime);

                Log.Info("db_WaveformArchive", $"Table {TABLE_NAME} created successfully");
            }
            else
            {
                Log.Info("db_WaveformArchive", $"Table {TABLE_NAME} already exists");
            }
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to create table: {ex.Message}");
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
        catch
        {
            return false;
        }
    }

    private void PeriodicDownloadFiles()
    {
        try
        {
            string deviceName = Owner.BrowseName;
            if (statusPeriodic != null) statusPeriodic.Value = $"Status: Checking {deviceName}...";

            var ipVariable = Owner.GetVariable("Val_IPAddress");
            if (ipVariable == null)
            {
                Log.Error("db_WaveformArchive", "Val_IPAddress variable not found on Owner object");
                return;
            }

            string ipAddress = ipVariable.Value;
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                Log.Error("db_WaveformArchive", $"IP address is empty for device {deviceName}");
                return;
            }

            ipAddress = ipAddress.Trim();
            if (!System.Net.IPAddress.TryParse(ipAddress, out _))
            {
                Log.Error("db_WaveformArchive", $"Invalid IP address format: '{ipAddress}' for device {deviceName}");
                return;
            }

            // Check device readiness (independent check each cycle)
            if (!IsDeviceReady())
            {
                return; // Skip this cycle, will check again next time
            }

            // Use direct FTP client (sequential for large waveform files)
            try
            {
                using (var client = new FtpClient(ipAddress))
                {
                    // Read FTP credentials from configuration variables
                    string ftpUsername = ReadFTPUsername();
                    string ftpPassword = ReadFTPPassword();
                    client.Credentials = new System.Net.NetworkCredential(ftpUsername, ftpPassword);
                    client.Config.ConnectTimeout = 30000;
                    client.Config.DataConnectionConnectTimeout = 30000;
                    client.Config.DataConnectionReadTimeout = 2700000;  // 45 minutes for large waveform files
                    client.Connect();

                    string remotePath = "/waveform";
                    if (!client.DirectoryExists(remotePath))
                    {
                        Log.Warning("db_WaveformArchive", $"Directory {remotePath} not found");
                        return; // Not an error
                    }

                    var items = client.GetListing(remotePath);
                    var wfmFiles = items.Where(item =>
                        item.Type == FtpObjectType.File &&
                        item.Name.EndsWith(".wfm", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    int totalFound = wfmFiles.Count;
                    int skippedCount = 0;
                    var newFiles = new List<string>();

                    // First pass: identify which files are new
                    foreach (var file in wfmFiles)
                    {
                        if (IsWaveformArchived(deviceName, file.Name))
                        {
                            skippedCount++;
                        }
                        else
                        {
                            newFiles.Add(file.Name);
                        }
                    }

                    int newFilesCount = newFiles.Count;

                    if (newFilesCount == 0)
                    {
                        if (statusPeriodic != null) statusPeriodic.Value = $"Status: No new files found (All {skippedCount} already archived)";
                        if (statusLastUpdate != null) statusLastUpdate.Value = $"Last Update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                        return;
                    }

                    // Update status showing new files found
                    if (statusPeriodic != null) statusPeriodic.Value = $"Status: {newFilesCount} New Files Found | (0/{newFilesCount}) Downloading...";
                    if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: Inprogress";

                    int downloadedCount = 0;
                    int failedCount = 0;

                    // Second pass: process only new files
                    for (int i = 0; i < newFiles.Count; i++)
                    {
                        string fileName = newFiles[i];

                        try
                        {
                            string deviceFolder = Path.Combine(archiveBasePath, deviceName);
                            if (!Directory.Exists(deviceFolder))
                            {
                                Directory.CreateDirectory(deviceFolder);
                            }

                            string localFilePath = Path.Combine(deviceFolder, fileName);
                            string remoteFilePath = $"/waveform/{fileName}";

                            var status = client.DownloadFile(localFilePath, remoteFilePath);

                            if (status == FtpStatus.Success)
                            {
                                long fileSize = new FileInfo(localFilePath).Length;

                                try
                                {
                                    var parser = new OptixWaveformParser.PM5000WaveformParser();
                                    parser.LoadFromFile(localFilePath);

                                    // ✅ Fetch event type from Power Quality Log
                                    string eventType = GetEventTypeForWaveform(fileName, ipAddress);

                                    string relativePath = Path.Combine("WaveformArchive", deviceName, fileName);

                                    if (StoreWaveformMetadata(
                                        deviceName,
                                        fileName,
                                        relativePath,
                                        (int)fileSize,
                                        parser.CaptureTime,
                                        parser.TotalCycles,
                                        0.0,
                                        0.0,
                                        eventType))
                                    {
                                        downloadedCount++;
                                        // Update progress
                                        int processed = downloadedCount + failedCount;
                                        if (statusPeriodic != null) statusPeriodic.Value = $"Status: {newFilesCount} New Files Found | ({processed}/{newFilesCount}) Downloading...";
                                    }
                                    else
                                    {
                                        failedCount++;
                                        // Update progress
                                        int processed = downloadedCount + failedCount;
                                        if (statusPeriodic != null) statusPeriodic.Value = $"Status: {newFilesCount} New Files Found | ({processed}/{newFilesCount}) Downloading...";
                                    }
                                }
                                catch (Exception parseEx)
                                {
                                    Log.Error("db_WaveformArchive", $"Failed to parse {fileName}: {parseEx.Message}");
                                    failedCount++;
                                    if (File.Exists(localFilePath))
                                    {
                                        File.Delete(localFilePath);
                                    }
                                    // Update progress
                                    int processed = downloadedCount + failedCount;
                                    if (statusPeriodic != null) statusPeriodic.Value = $"Status: {newFilesCount} New Files Found | ({processed}/{newFilesCount}) Downloading...";
                                }
                            }
                            else
                            {
                                Log.Warning("db_WaveformArchive", $"Download failed for {fileName}: {status}");
                                failedCount++;
                                // Update progress
                                int processed = downloadedCount + failedCount;
                                if (statusPeriodic != null) statusPeriodic.Value = $"Status: {newFilesCount} New Files Found | ({processed}/{newFilesCount}) Downloading...";
                            }
                        }
                        catch (Exception downloadEx)
                        {
                            Log.Warning("db_WaveformArchive", $"Error downloading {fileName}: {downloadEx.Message}");
                            failedCount++;
                            // Update progress
                            int processed = downloadedCount + failedCount;
                            if (statusPeriodic != null) statusPeriodic.Value = $"Status: {newFilesCount} New Files Found | ({processed}/{newFilesCount}) Downloading...";
                        }
                    }

                    // Final status
                    string finalStatus = $"Status: {newFilesCount} New Files Found | ✓ {downloadedCount} Downloaded";
                    if (failedCount > 0)
                    {
                        finalStatus += $" | ✗ {failedCount} Failed";
                    }
                    
                    if (statusPeriodic != null) statusPeriodic.Value = finalStatus;
                    if (statusLastUpdate != null) statusLastUpdate.Value = $"Last Update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                }
            }
            catch (Exception ex)
            {
                Log.Error("db_WaveformArchive", $"Periodic download failed: {ex.Message}");
                if (statusPeriodic != null) statusPeriodic.Value = $"Status: ERROR - {ex.Message}";
                if (statusLastUpdate != null) statusLastUpdate.Value = $"Last Update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            }
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error in periodic download: {ex.Message}");
            if (statusPeriodic != null) statusPeriodic.Value = $"Status: EXCEPTION - {ex.Message}";
            if (statusLastUpdate != null) statusLastUpdate.Value = $"Last Update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
    }

    private bool StoreWaveformMetadata(
        string deviceName,
        string fileName,
        string filePath,
        int fileSize,
        DateTime captureDateTime,
        int totalCycles,
        double maxVoltage,
        double maxCurrent,
        string eventType)
    {
        try
        {
            if (!CheckStorageQuota(fileSize))
            {
                Log.Warning("db_WaveformArchive", "Storage quota exceeded - cleaning up old files");
                CleanupOldWaveforms(fileSize);
            }

            var table = waveformStore.Tables.Get(TABLE_NAME);
            if (table == null)
            {
                Log.Error("db_WaveformArchive", $"Table {TABLE_NAME} not found");
                return false;
            }

            // ✅ DO NOT include Id column - it will auto-increment
            string[] columns = {
                "DeviceName", "FileName", "FilePath", "FileSize",
                "CaptureDateTime", "TotalCycles", "MaxVoltage", "MaxCurrent",
                "EventType", "CreatedAt"
            };

            var values = new object[1, columns.Length];
            values[0, 0] = deviceName;
            values[0, 1] = fileName;
            values[0, 2] = filePath;
            values[0, 3] = fileSize;
            values[0, 4] = captureDateTime;
            values[0, 5] = totalCycles;
            values[0, 6] = maxVoltage;
            values[0, 7] = maxCurrent;
            values[0, 8] = eventType ?? "";
            values[0, 9] = DateTime.Now;

            // ✅ Use Table.Insert() - this is the correct FTOptix way
            table.Insert(columns, values);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to store metadata: {ex.Message}");
            return false;
        }
    }

    [ExportMethod]
    public bool IsWaveformArchived(string deviceName, string fileName)
    {
        try
        {
            string safeDeviceName = SanitizeSqlString(deviceName);
            string safeFileName = SanitizeSqlString(fileName);

            var query = $"SELECT FilePath FROM {TABLE_NAME} WHERE DeviceName = '{safeDeviceName}' AND FileName = '{safeFileName}'";
            waveformStore.Query(query, out string[] header, out object[,] resultSet);

            // Check if record exists in database
            if (resultSet.GetLength(0) > 0 && resultSet[0, 0] != null)
            {
                // ✅ Also check if physical file exists
                string relativePath = resultSet[0, 0].ToString();
                var appDirUri = new ResourceUri("%APPLICATIONDIR%\\");
                string fullPath = Path.Combine(appDirUri.Uri, relativePath);

                if (File.Exists(fullPath))
                {
                    return true; // Both DB record and file exist
                }
                else
                {
                    // ✅ File missing - clean up orphaned database record
                    Log.Warning("db_WaveformArchive", $"File missing but DB record exists: {fileName}. Cleaning up orphaned record.");
                    DeleteWaveformMetadata(deviceName, fileName);
                    return false;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error checking archive: {ex.Message}");
            return false;
        }
    }

    [ExportMethod]
    public string GetArchivedWaveformPath(string deviceName, string fileName)
    {
        try
        {
            string safeDeviceName = SanitizeSqlString(deviceName);
            string safeFileName = SanitizeSqlString(fileName);

            var query = $"SELECT FilePath FROM {TABLE_NAME} WHERE DeviceName = '{safeDeviceName}' AND FileName = '{safeFileName}'";
            waveformStore.Query(query, out string[] header, out object[,] resultSet);

            if (resultSet.GetLength(0) > 0 && resultSet[0, 0] != null)
            {
                string relativePath = resultSet[0, 0].ToString();
                var appDirUri = new ResourceUri("%APPLICATIONDIR%\\");
                string fullPath = Path.Combine(appDirUri.Uri, relativePath);

                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
                else
                {
                    Log.Warning("db_WaveformArchive", $"File not found: {fullPath}");
                    DeleteWaveformMetadata(deviceName, fileName);
                    return null;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to get path: {ex.Message}");
            return null;
        }
    }

    [ExportMethod]
    public byte[] LoadWaveformFromArchive(string deviceName, string fileName)
    {
        try
        {
            string filePath = GetArchivedWaveformPath(deviceName, fileName);
            if (filePath != null && File.Exists(filePath))
            {
                byte[] wfmBytes = File.ReadAllBytes(filePath);
                Log.Info("db_WaveformArchive", $"Loaded {fileName} from archive ({wfmBytes.Length / 1024} KB)");
                return wfmBytes;
            }

            Log.Warning("db_WaveformArchive", $"{fileName} not found in archive for {deviceName}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to load from archive: {ex.Message}");
            return null;
        }
    }

    [ExportMethod]
    public List<WaveformArchiveInfo> GetArchivedWaveforms(string deviceName, int maxRecords = 1000)
    {
        var results = new List<WaveformArchiveInfo>();

        try
        {
            string safeDeviceName = SanitizeSqlString(deviceName);

            var query = $@"
                SELECT Id, FileName, FilePath, FileSize, CaptureDateTime, TotalCycles,
                       MaxVoltage, MaxCurrent, EventType, CreatedAt
                FROM {TABLE_NAME}
                WHERE DeviceName = '{safeDeviceName}'
                ORDER BY CaptureDateTime DESC
                LIMIT {maxRecords}";

            waveformStore.Query(query, out string[] headers, out object[,] resultSet);

            for (int i = 0; i < resultSet.GetLength(0); i++)
            {
                results.Add(new WaveformArchiveInfo
                {
                    Id = Convert.ToInt32(resultSet[i, 0]),
                    FileName = resultSet[i, 1]?.ToString() ?? "",
                    FilePath = resultSet[i, 2]?.ToString() ?? "",
                    FileSize = Convert.ToInt32(resultSet[i, 3]),
                    CaptureDateTime = DateTime.Parse(resultSet[i, 4]?.ToString() ?? DateTime.MinValue.ToString()),
                    TotalCycles = Convert.ToInt32(resultSet[i, 5]),
                    MaxVoltage = Convert.ToDouble(resultSet[i, 6] ?? 0.0),
                    MaxCurrent = Convert.ToDouble(resultSet[i, 7] ?? 0.0),
                    EventType = resultSet[i, 8]?.ToString() ?? "",
                    ArchivedAt = DateTime.Parse(resultSet[i, 9]?.ToString() ?? DateTime.MinValue.ToString())
                });
            }

            Log.Info("db_WaveformArchive", $"Found {results.Count} archived waveforms for {deviceName}");
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to query archive: {ex.Message}");
        }

        return results;
    }

    private bool CheckStorageQuota(int newFileSize)
    {
        try
        {
            var query = $"SELECT SUM(FileSize) FROM {TABLE_NAME}";
            waveformStore.Query(query, out string[] header, out object[,] resultSet);

            if (resultSet.GetLength(0) > 0 && resultSet[0, 0] != DBNull.Value)
            {
                long totalSize = Convert.ToInt64(resultSet[0, 0]);
                return (totalSize + newFileSize) < maxArchiveSizeBytes;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private void CleanupOldWaveforms(int spaceNeeded)
    {
        try
        {
            var query = $@"
                SELECT DeviceName, FileName, FilePath
                FROM {TABLE_NAME}
                ORDER BY CaptureDateTime ASC
                LIMIT (SELECT COUNT(*) / 10 FROM {TABLE_NAME})";

            waveformStore.Query(query, out string[] headers, out object[,] resultSet);

            int deletedCount = 0;
            for (int i = 0; i < resultSet.GetLength(0); i++)
            {
                string deviceName = resultSet[i, 0]?.ToString();
                string fileName = resultSet[i, 1]?.ToString();
                string relativePath = resultSet[i, 2]?.ToString();

                if (!string.IsNullOrEmpty(relativePath))
                {
                    var appDirUri = new ResourceUri("%APPLICATIONDIR%\\");
                    string fullPath = Path.Combine(appDirUri.Uri, relativePath);

                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }

                    DeleteWaveformMetadata(deviceName, fileName);
                    deletedCount++;
                }
            }

            Log.Info("db_WaveformArchive", $"Cleaned up {deletedCount} old waveforms");
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Cleanup failed: {ex.Message}");
        }
    }

    [ExportMethod]
    public bool DeleteWaveform(string deviceName, string fileName)
    {
        try
        {
            string filePath = GetArchivedWaveformPath(deviceName, fileName);
            if (filePath != null && File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            DeleteWaveformMetadata(deviceName, fileName);
            Log.Info("db_WaveformArchive", $"Deleted {fileName} from archive");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to delete: {ex.Message}");
            return false;
        }
    }

    private void DeleteWaveformMetadata(string deviceName, string fileName)
    {
        string safeDeviceName = SanitizeSqlString(deviceName);
        string safeFileName = SanitizeSqlString(fileName);
        var query = $"DELETE FROM {TABLE_NAME} WHERE DeviceName = '{safeDeviceName}' AND FileName = '{safeFileName}'";
        waveformStore.Query(query, out string[] header, out object[,] resultSet);
    }

    [ExportMethod]
    public ArchiveStatistics GetArchiveStatistics()
    {
        var stats = new ArchiveStatistics();

        try
        {
            var query = $@"
                SELECT
                    COUNT(*) as TotalFiles,
                    SUM(FileSize) as TotalSize,
                    AVG(FileSize) as AvgSize,
                    MIN(CaptureDateTime) as OldestCapture,
                    MAX(CaptureDateTime) as NewestCapture
                FROM {TABLE_NAME}";

            waveformStore.Query(query, out string[] headers, out object[,] resultSet);

            if (resultSet.GetLength(0) > 0)
            {
                stats.TotalFiles = Convert.ToInt32(resultSet[0, 0] ?? 0);
                stats.TotalSizeMB = Convert.ToInt64(resultSet[0, 1] ?? 0L) / (1024.0 * 1024.0);
                stats.AverageSizeKB = Convert.ToInt64(resultSet[0, 2] ?? 0L) / 1024.0;
                stats.OldestCapture = resultSet[0, 3] != DBNull.Value
                    ? DateTime.Parse(resultSet[0, 3].ToString())
                    : DateTime.MinValue;
                stats.NewestCapture = resultSet[0, 4] != DBNull.Value
                    ? DateTime.Parse(resultSet[0, 4].ToString())
                    : DateTime.MinValue;
                stats.QuotaUsedPercent = (stats.TotalSizeMB / (maxArchiveSizeBytes / (1024.0 * 1024.0))) * 100.0;
            }
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to get statistics: {ex.Message}");
        }

        return stats;
    }

    private string SanitizeSqlString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        return input.Replace("'", "''");
    }

    [ExportMethod]
    public void DropTable()
    {
        try
        {
            if (TableExists(waveformStore, TABLE_NAME))
            {
                waveformStore.Query($"DROP TABLE {TABLE_NAME}", out _, out _);
                Log.Info($"Table '{TABLE_NAME}' dropped successfully.");
            }
            else
            {
                Log.Info($"Table '{TABLE_NAME}' does not exist.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error dropping table '{TABLE_NAME}': {ex.Message}");
        }
    }

    private void PerformIntegrityCheck()
    {
        try
        {
            Log.Info("db_WaveformArchive", "Starting integrity check...");

            var query = $"SELECT Id, DeviceName, FileName, FilePath FROM {TABLE_NAME}";
            waveformStore.Query(query, out string[] headers, out object[,] resultSet);

            int orphanedRecords = 0;
            var appDirUri = new ResourceUri("%APPLICATIONDIR%\\").Uri;

            for (int i = 0; i < resultSet.GetLength(0); i++)
            {
                string deviceName = resultSet[i, 1]?.ToString();
                string fileName = resultSet[i, 2]?.ToString();
                string relativePath = resultSet[i, 3]?.ToString();

                if (!string.IsNullOrEmpty(relativePath))
                {
                    string fullPath = Path.Combine(appDirUri, relativePath);

                    // ✅ Check if file actually exists
                    if (!File.Exists(fullPath))
                    {
                        Log.Warning("db_WaveformArchive", $"Orphaned record found: {fileName} (file missing)");
                        DeleteWaveformMetadata(deviceName, fileName);
                        orphanedRecords++;
                    }
                }
            }

            if (orphanedRecords > 0)
            {
                Log.Info("db_WaveformArchive", $"Integrity check complete. Cleaned up {orphanedRecords} orphaned record(s)");
            }
            else
            {
                Log.Info("db_WaveformArchive", "Integrity check complete. No issues found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Integrity check failed: {ex.Message}");
        }
    }

    private string GetEventTypeForWaveform(string fileName, string ipAddress)
    {
        try
        {
            using (var client = new FtpClient(ipAddress))
            {
                // Read FTP credentials from configuration variables
                string ftpUsername = ReadFTPUsername();
                string ftpPassword = ReadFTPPassword();
                client.Credentials = new System.Net.NetworkCredential(ftpUsername, ftpPassword);
                client.Config.ConnectTimeout = 10000;
                client.Config.DataConnectionConnectTimeout = 10000;
                client.Config.DataConnectionReadTimeout = 15000;
                client.Config.EncryptionMode = FtpEncryptionMode.None;
                client.Config.DataConnectionType = FtpDataConnectionType.PORT;

                client.Connect();

                string remoteCsvPath = "/LoggingResults/Power_Quality_Log.csv";
                if (!client.FileExists(remoteCsvPath))
                {
                    Log.Warning("db_WaveformArchive", $"Power_Quality_Log.csv not found at {remoteCsvPath}");
                    client.Disconnect();
                    return "";
                }

                // ✅ Use DownloadStream to read file into memory
                using (var ms = new MemoryStream())
                {
                    client.DownloadStream(ms, remoteCsvPath);
                    ms.Position = 0;

                    using (var sr = new StreamReader(ms))
                    {
                        string csvContent = sr.ReadToEnd();

                        // --- Parse filename timestamp ---
                        var match = System.Text.RegularExpressions.Regex.Match(
                            fileName,
                            @"_(\d{8})_(\d{6})_(\d{6})_(\d{2})\.wfm"
                        );

                        if (!match.Success) return "";

                        string yearMonthDay = match.Groups[1].Value;
                        string hourMinSec = match.Groups[2].Value;
                        string msUs = match.Groups[3].Value;

                        string year = yearMonthDay.Substring(0, 4);
                        string monthDay = yearMonthDay.Substring(4, 4);
                        string hourMin = hourMinSec.Substring(0, 4);
                        string sec = hourMinSec.Substring(4, 2);
                        string milliSec = msUs.Substring(0, 3);
                        string microSec = msUs.Substring(3, 3);

                        var lines = csvContent.Split('\n');
                        var matchingEvents = new List<string>();

                        foreach (var line in lines.Skip(1))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var columns = line.Split(',');
                            if (columns.Length < 18) continue;

                            if (columns[13] == year &&
                                columns[14].PadLeft(4, '0') == monthDay &&
                                columns[15].PadLeft(4, '0') == hourMin &&
                                columns[16].PadLeft(5, '0') == $"{sec}{milliSec}" &&
                                columns[17].PadLeft(3, '0') == microSec)
                            {
                                matchingEvents.Add(columns[1]);
                            }
                        }

                        client.Disconnect();

                        return matchingEvents.Any()
                            ? string.Join(", ", matchingEvents.Distinct().OrderBy(e => e))
                            : "";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("db_WaveformArchive", $"FTP event-type fetch failed for {fileName}: {ex.Message}");
        }

        return "";
    }

    /// <summary>
    /// Reads storage path variable and resolves to proper ResourceUri
    /// Supports: %APPLICATIONDIR%, %PROJECTDIR%, %USB1%, %USB2%, etc.
    /// </summary>

    private string ReadStoragePath(string variableName, string defaultPath)
    {
        try
        {
            var variable = Owner.GetVariable(variableName);
            if (variable != null)
            {
                string configuredPath = variable.Value;
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    // Resolve the path shortcut to actual URI
                    return ResolvePathShortcut(configuredPath.Trim());
                }
            }
            
            Log.Warning("db_WaveformArchive", $"{variableName} not found or empty. Using default: {defaultPath}");
            return ResolvePathShortcut(defaultPath);
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Error reading {variableName}: {ex.Message}. Using default: {defaultPath}");
            return ResolvePathShortcut(defaultPath);
        }
    }


    private string ResolvePathShortcut(string pathShortcut)
    {
        try
        {
            // Handle USB paths: %USB1%, %USB2%, etc.
            if (pathShortcut.StartsWith("%USB", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(pathShortcut, @"%USB(\d+)%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    uint usbNumber = uint.Parse(match.Groups[1].Value);
                    string remainingPath = pathShortcut.Substring(match.Value.Length).TrimStart('\\', '/');
                    
                    try
                    {
                        string usbUri = ResourceUri.FromUSBRelativePath(usbNumber, remainingPath).Uri;
                        Log.Info("db_WaveformArchive", $"Resolved USB path: {pathShortcut} -> {usbUri}");
                        return usbUri;
                    }
                    catch (Exception usbEx)
                    {
                        Log.Error("db_WaveformArchive", $"USB{usbNumber} not available: {usbEx.Message}. Falling back to %APPLICATIONDIR%");
                        return new ResourceUri("%APPLICATIONDIR%\\").Uri;
                    }
                }
            }
            
            // Handle %APPLICATIONDIR%
            if (pathShortcut.StartsWith("%APPLICATIONDIR%", StringComparison.OrdinalIgnoreCase))
            {
                return new ResourceUri("%APPLICATIONDIR%\\").Uri;
            }
            
            // Handle %PROJECTDIR%
            if (pathShortcut.StartsWith("%PROJECTDIR%", StringComparison.OrdinalIgnoreCase))
            {
                return new ResourceUri("%PROJECTDIR%\\").Uri;
            }
            
            // If no shortcut detected, treat as literal path
            Log.Warning("db_WaveformArchive", $"Unknown path format '{pathShortcut}', using as literal path");
            return pathShortcut;
        }
        catch (Exception ex)
        {
            Log.Error("db_WaveformArchive", $"Failed to resolve path '{pathShortcut}': {ex.Message}");
            return new ResourceUri("%APPLICATIONDIR%\\").Uri;
        }
    }

    [ExportMethod]
    public void DropUnifiedTable()
    {
                waveformStore.RemoveTable(TABLE_NAME);        
                Log.Info($"db_WaveformArchive-{Owner.BrowseName}", $"Unified table '{TABLE_NAME}' dropped successfully.");
      
    }

}

public class WaveformArchiveInfo
{
    public int Id { get; set; }
    public string FileName { get; set; }
    public string FilePath { get; set; }
    public int FileSize { get; set; }
    public DateTime CaptureDateTime { get; set; }
    public int TotalCycles { get; set; }
    public double MaxVoltage { get; set; }
    public double MaxCurrent { get; set; }
    public string EventType { get; set; }
    public DateTime ArchivedAt { get; set; }
}

public class ArchiveStatistics
{
    public int TotalFiles { get; set; }
    public double TotalSizeMB { get; set; }
    public double AverageSizeKB { get; set; }
    public DateTime OldestCapture { get; set; }
    public DateTime NewestCapture { get; set; }
    public double QuotaUsedPercent { get; set; }
}
