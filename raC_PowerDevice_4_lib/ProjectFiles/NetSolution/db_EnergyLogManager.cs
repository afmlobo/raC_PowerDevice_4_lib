#region Using directives
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using DeviceLogin;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SQLiteStore;
using FTOptix.SerialPort;
#endregion

public class db_EnergyLogManager : BaseNetLogic
{
    private Store energyStore;
    private PeriodicTask periodicEnergyTask;
    private LongRunningTask initialIngestionTask;
    private LongRunningTask backfillTask;
    private const string TABLE_NAME = "EnergyLog_AllDevices";
    private const string TRACKING_TABLE_NAME = "EnergyLog_ProcessedFiles";
    private const int FILES_TO_CHECK_PERIODIC = 3;  // Check last 3 files for rollover scenarios
    private const int MAX_RETRIES = 3;               // Retry count for failed HTTP downloads
    private const int INITIAL_LOAD_DELAY_MS = 3000;  // 3 seconds delay between files during initial load
    private const int DEVICE_OFFLINE_RETRY_MS = 30000; // 30 seconds wait when device is offline during backfill
    
    private string deviceName;
    private string ipAddress;
    private string baseHttpUrl;
    private string portalUrl;
    private HttpClient httpClient;
    private DeviceLoginManager loginManager;
    private IUAVariable enableEnergyLogging;
    private IUAVariable statusInitial;      // UI: First-run progress (e.g., "Processing file 5/50")
    private IUAVariable statusPeriodic;     // UI: Periodic status (e.g., "Checking..." or "Idle")
    private IUAVariable statusLastUpdate;   // UI: Last update timestamp
    private bool initialLoadComplete = false;
    private bool schemaInitialized = false;
    private bool loginChecked = false;
    private volatile bool backfillRunning = false;   // Tracks if background backfill is active (informational only, does NOT block periodic)
    private DateTime lastPeriodicUpdateTime = DateTime.MinValue;
    private DateTime lastDeviceReadyCheck = DateTime.MinValue;
    private bool lastDeviceReadyStatus = false;
    private readonly object lockObject = new object();

    public override void Start()
    {
        try
        {
            energyStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (energyStore == null)
            {
                Log.Error("db_EnergyLogManager", "PowerQuality DB not found");
                return;
            }

            deviceName = Owner.BrowseName;
            ipAddress = Owner.GetVariable("Val_IPAddress")?.Value;
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                Log.Error("db_EnergyLogManager", "IP address missing");
                return;
            }

            // Initialize HTTP client and URLs
            baseHttpUrl = $"http://{ipAddress}/LoggingResults";
            portalUrl = $"http://{ipAddress}/logresults/Energy_Log.shtm";
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(120); // 2 minutes - PM5000 embedded web server can be slow for large CSV files

            // Initialize DeviceLoginManager
            loginManager = new DeviceLoginManager(ipAddress, Owner, deviceName);

            CreateTableIfNotExists();

            // Read enable/disable logging variable
            enableEnergyLogging = Owner.GetVariable("Set_EnableEnergyLogLogging");
            if (enableEnergyLogging == null)
            {
                Log.Warning("db_EnergyLogManager", "Set_EnableEnergyLogLogging variable not found, defaulting to enabled");
                enableEnergyLogging = InformationModel.MakeVariable("Set_EnableEnergyLogLogging", OpcUa.DataTypes.Boolean);
                enableEnergyLogging.Value = true;
                Owner.Add(enableEnergyLogging);
            }

            // Subscribe to enable/disable changes
            enableEnergyLogging.VariableChange += EnableEnergyLogging_VariableChange;

            // Create or get status UI variables
            statusInitial = Owner.GetVariable("Val_EnergyLog_InitStatus");
            if (statusInitial == null)
            {
                statusInitial = InformationModel.MakeVariable("Val_EnergyLog_InitStatus", OpcUa.DataTypes.String);
                statusInitial.Value = "Init Status: Initializing...";
                Owner.Add(statusInitial);
            }

            statusPeriodic = Owner.GetVariable("Val_EnergyLog_PeriodicStatus");
            if (statusPeriodic == null)
            {
                statusPeriodic = InformationModel.MakeVariable("Val_EnergyLog_PeriodicStatus", OpcUa.DataTypes.String);
                statusPeriodic.Value = "Periodic: Idle";
                Owner.Add(statusPeriodic);
            }

            statusLastUpdate = Owner.GetVariable("Val_EnergyLog_LastUpdate");
            if (statusLastUpdate == null)
            {
                statusLastUpdate = InformationModel.MakeVariable("Val_EnergyLog_LastUpdate", OpcUa.DataTypes.String);
                statusLastUpdate.Value = "Last Update: Never";
                Owner.Add(statusLastUpdate);
            }

            if (!enableEnergyLogging.Value)
            {
                Log.Debug("db_EnergyLogManager", "Energy logging is disabled (Set_EnableEnergyLogLogging = False)");
                statusInitial.Value = "Init Status: Energy Logging Is Disabled";
                statusPeriodic.Value = "Periodic: Energy Logging Is Disabled";
                statusLastUpdate.Value = "Last Update: N/A";
                return;
            }

            Log.Debug("db_EnergyLogManager", $"Initialized with HTTP portal: {portalUrl}");

            // Start initial ingestion in LongRunningTask (non-blocking)
            initialIngestionTask = new LongRunningTask(TaskInitializeAndDownload, LogicObject);
            initialIngestionTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Start failed: {ex.Message}");
        }
    }

    public override void Stop()
    {
        initialIngestionTask?.Dispose();
        initialIngestionTask = null;

        backfillTask?.Dispose();
        backfillTask = null;

        periodicEnergyTask?.Cancel();
        periodicEnergyTask?.Dispose();
        periodicEnergyTask = null;

        if (enableEnergyLogging != null)
            enableEnergyLogging.VariableChange -= EnableEnergyLogging_VariableChange;

        httpClient?.Dispose();
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
                    Log.Debug($"EnergyLogManager_HTTP-{deviceName}", "Device is now Ready");
                else
                    Log.Debug($"EnergyLogManager_HTTP-{deviceName}", "Device is Not Ready (HTTP check failed)");
                
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
                Log.Debug($"EnergyLogManager_HTTP-{deviceName}", $"Device communication failed: {ex.Message}");
                lastDeviceReadyStatus = false;
            }
            
            lastDeviceReadyCheck = DateTime.Now;
            return false;
        }
    }

    private void TaskInitializeAndDownload(LongRunningTask task)
    {
        try
        {
            // Initialize schema (tables)
            CreateTableIfNotExists();
            schemaInitialized = true;
            Log.Debug($"EnergyLogManager_HTTP-{deviceName}", "Database schema initialized");
            
            // Check device readiness first (independent HTTP check)
            statusInitial.Value = "Init Status: Checking device...";
            if (!IsDeviceReady())
            {
                statusInitial.Value = "Init Status: Device Not Ready";
                Log.Warning($"EnergyLogManager_HTTP-{deviceName}", "Device not ready at startup. Will retry in periodic checks.");
                
                // Start periodic task anyway — it will handle device readiness and login each cycle
                StartPeriodicTask();
                return;
            }
            
            // Perform login check once at startup (only if device is ready)
            if (!loginChecked)
            {
                statusInitial.Value = "Init Status: Logging in...";
                bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
                loginChecked = true;
                
                if (!isLoggedIn)
                {
                    statusInitial.Value = "Init Status: Login failed";
                    Log.Error($"EnergyLogManager_HTTP-{deviceName}", "Cannot initialize EnergyLogManager due to login failure.");
                    
                    // Start periodic task anyway — login might succeed later
                    StartPeriodicTask();
                    return;
                }
                Log.Debug($"EnergyLogManager_HTTP-{deviceName}", "Successfully logged in to device");
            }

            // === NEW ARCHITECTURE: Start periodic FIRST, then backfill in background ===
            // This ensures new data flows immediately, regardless of how many historical files exist
            
            // 1. Start periodic task IMMEDIATELY — always checks latest files every cycle
            StartPeriodicTask();
            
            // 2. Check if historical backfill is needed
            DateTime latestInDb = GetLastProcessedTimestamp();
            if (latestInDb > DateTime.MinValue)
            {
                Log.Debug($"EnergyLogManager_HTTP-{deviceName}", $"Database already contains data (latest: {latestInDb:yyyy-MM-dd HH:mm:ss}). Checking for unprocessed historical files.");
                statusInitial.Value = "Init Status: Complete (data exists)";
                initialLoadComplete = true;
            }
            else
            {
                statusInitial.Value = "Init Status: Starting...";
                initialLoadComplete = true; // Periodic can run immediately, backfill handles history
            }
            
            // 3. Start background backfill for historical files (runs independently, doesn't block periodic)
            backfillTask = new LongRunningTask(BackfillHistoricalFiles, LogicObject);
            backfillTask.Start();
            Log.Debug($"EnergyLogManager_HTTP-{deviceName}", "Background backfill task started");
        }
        catch (Exception ex)
        {
            Log.Error($"EnergyLogManager_HTTP-{deviceName}", $"Init exception: {ex.Message}");
            statusInitial.Value = $"Init Status: Error: {ex.Message}";
        }
    }

    private void EnableEnergyLogging_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            if (periodicEnergyTask == null)
            {
                Log.Debug("db_EnergyLogManager", "Energy logging enabled - initializing and starting");
                
                // Update status labels to show enabled state
                if (statusInitial != null) statusInitial.Value = "Init Status: Initializing...";
                if (statusPeriodic != null) statusPeriodic.Value = "Periodic: Waiting for init...";
                
                // Run full initialization (creates tables, starts periodic, starts backfill)
                initialIngestionTask = new LongRunningTask(TaskInitializeAndDownload, LogicObject);
                initialIngestionTask.Start();
            }
        }
        else
        {
            if (periodicEnergyTask != null)
            {
                periodicEnergyTask.Cancel();
                periodicEnergyTask.Dispose();
                periodicEnergyTask = null;
                Log.Debug("db_EnergyLogManager", "Energy logging disabled - stopped periodic download");
            }
            if (backfillTask != null)
            {
                backfillTask.Dispose();
                backfillTask = null;
                Log.Debug("db_EnergyLogManager", "Energy logging disabled - stopped backfill task");
            }
            // Update status labels to show disabled state
            if (statusInitial != null) statusInitial.Value = "Init Status: Energy Logging Is Disabled";
            if (statusPeriodic != null) statusPeriodic.Value = "Periodic: Energy Logging Is Disabled";
            if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: N/A";
        }
    }

    private void StartPeriodicTask()
    {
        try
        {
            int checkPeriodSeconds = ReadDurationVariable("Cfg_EnergyLogCheckPeriodSeconds", 900); // Default 15 min
            periodicEnergyTask = new PeriodicTask(DownloadAndStoreNewEnergyLogs, checkPeriodSeconds * 1000, LogicObject);
            periodicEnergyTask.Start();
            Log.Debug("db_EnergyLogManager", $"Started periodic energy log download every {checkPeriodSeconds} seconds for {deviceName}");
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Failed to start periodic task: {ex.Message}");
        }
    }

    private void CreateTableIfNotExists()
    {
        try
        {
            CreateEnergyLogTable();
            CreateFileTrackingTable();
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Failed to create tables: {ex.Message}");
        }
    }

    private void CreateEnergyLogTable()
    {
        try
        {
            if (!TableExists(energyStore, TABLE_NAME))
            {
                energyStore.AddTable(TABLE_NAME);

                // Primary Key - Auto-increment unique ID
                energyStore.AddColumn(TABLE_NAME, "ID", OpcUa.DataTypes.Int64);

                // Metadata columns
                energyStore.AddColumn(TABLE_NAME, "DeviceName", OpcUa.DataTypes.String);
                energyStore.AddColumn(TABLE_NAME, "Timestamp", OpcUa.DataTypes.DateTime);
                energyStore.AddColumn(TABLE_NAME, "RecordID", OpcUa.DataTypes.Int32);

                // Status Count columns (8 columns)
                energyStore.AddColumn(TABLE_NAME, "Status_1_Count_xM", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_1_Count_x1", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_2_Count_xM", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_2_Count_x1", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_3_Count_xM", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_3_Count_x1", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_4_Count_xM", OpcUa.DataTypes.Int32);
                energyStore.AddColumn(TABLE_NAME, "Status_4_Count_x1", OpcUa.DataTypes.Int32);

                // Active Energy - Forward (Delivered)
                energyStore.AddColumn(TABLE_NAME, "GWh_Fwd", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kWh_Fwd", OpcUa.DataTypes.Double);

                // Active Energy - Reverse (Received)
                energyStore.AddColumn(TABLE_NAME, "GWh_Rev", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kWh_Rev", OpcUa.DataTypes.Double);

                // Active Energy - Net
                energyStore.AddColumn(TABLE_NAME, "GWh_Net", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kWh_Net", OpcUa.DataTypes.Double);

                // Reactive Energy - Forward
                energyStore.AddColumn(TABLE_NAME, "GVARh_Fwd", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kVARh_Fwd", OpcUa.DataTypes.Double);

                // Reactive Energy - Reverse
                energyStore.AddColumn(TABLE_NAME, "GVARh_Rev", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kVARh_Rev", OpcUa.DataTypes.Double);

                // Reactive Energy - Net
                energyStore.AddColumn(TABLE_NAME, "GVARh_Net", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kVARh_Net", OpcUa.DataTypes.Double);

                // Apparent Energy
                energyStore.AddColumn(TABLE_NAME, "GVAh", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kVAh", OpcUa.DataTypes.Double);

                // Demand values
                energyStore.AddColumn(TABLE_NAME, "kW_Demand", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kVAR_Demand", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "kVA_Demand", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Demand_PF", OpcUa.DataTypes.Double);

                // Projected Demand values
                energyStore.AddColumn(TABLE_NAME, "Projected_kW_Demand", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Projected_kVAR_Demand", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Projected_kVA_Demand", OpcUa.DataTypes.Double);

                // Calculated totals (for convenience)
                energyStore.AddColumn(TABLE_NAME, "Total_kWh_Fwd", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Total_kWh_Rev", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Total_kWh_Net", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Total_kVARh_Fwd", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Total_kVARh_Rev", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Total_kVARh_Net", OpcUa.DataTypes.Double);
                energyStore.AddColumn(TABLE_NAME, "Total_kVAh", OpcUa.DataTypes.Double);

                energyStore.AddColumn(TABLE_NAME, "SourceFileName", OpcUa.DataTypes.String);
                energyStore.AddColumn(TABLE_NAME, "CreatedAt", OpcUa.DataTypes.DateTime);

                // Create indexes for performance
                try
                {
                    energyStore.Query($"CREATE UNIQUE INDEX IF NOT EXISTS idx_energy_pk ON {TABLE_NAME}(ID)", out _, out _);
                    energyStore.Query($"CREATE UNIQUE INDEX IF NOT EXISTS idx_energy_device_timestamp ON {TABLE_NAME}(DeviceName, Timestamp)", out _, out _);
                    energyStore.Query($"CREATE INDEX IF NOT EXISTS idx_energy_device ON {TABLE_NAME}(DeviceName)", out _, out _);
                    energyStore.Query($"CREATE INDEX IF NOT EXISTS idx_energy_timestamp ON {TABLE_NAME}(Timestamp DESC)", out _, out _);
                    energyStore.Query($"CREATE INDEX IF NOT EXISTS idx_energy_recordid ON {TABLE_NAME}(RecordID)", out _, out _);
                    energyStore.Query($"CREATE INDEX IF NOT EXISTS idx_energy_sourcefile ON {TABLE_NAME}(SourceFileName)", out _, out _);
                    Log.Debug("db_EnergyLogManager", "Indexes created successfully");
                }
                catch (Exception ex)
                {
                    Log.Warning("db_EnergyLogManager", $"Index creation warning: {ex.Message}");
                }

                Log.Debug("db_EnergyLogManager", $"Table {TABLE_NAME} created successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Failed to create energy log table: {ex.Message}");
        }
    }

    private void CreateFileTrackingTable()
    {
        try
        {
            if (!TableExists(energyStore, TRACKING_TABLE_NAME))
            {
                energyStore.AddTable(TRACKING_TABLE_NAME);
                energyStore.AddColumn(TRACKING_TABLE_NAME, "DeviceName", OpcUa.DataTypes.String);
                energyStore.AddColumn(TRACKING_TABLE_NAME, "FileName", OpcUa.DataTypes.String);
                energyStore.AddColumn(TRACKING_TABLE_NAME, "ProcessedTimestamp", OpcUa.DataTypes.DateTime);
                energyStore.AddColumn(TRACKING_TABLE_NAME, "RowCount", OpcUa.DataTypes.Int32);
                Log.Debug($"EnergyLogManager_HTTP-{deviceName}", $"Created tracking table: {TRACKING_TABLE_NAME}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Failed to create tracking table: {ex.Message}");
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
            energyStore.Query(query, out _, out object[,] resultSet);
            if (resultSet != null && resultSet.GetLength(0) > 0 && resultSet[0, 0] != null && resultSet[0, 0] != DBNull.Value)
                return DateTime.Parse(resultSet[0, 0].ToString());
        }
        catch (Exception ex)
        {
            Log.Warning("db_EnergyLogManager", $"Could not get last timestamp: {ex.Message}");
        }
        return DateTime.MinValue;
    }

    private bool IsFileFullyProcessed(string fileName)
    {
        try
        {
            var query = $"SELECT COUNT(*) FROM {TRACKING_TABLE_NAME} WHERE DeviceName = '{deviceName}' AND FileName = '{fileName}'";
            energyStore.Query(query, out _, out object[,] result);
            return result != null && result.GetLength(0) > 0 && Convert.ToInt32(result[0, 0]) > 0;
        }
        catch
        {
            return false;
        }
    }

    private void MarkFileAsFullyProcessed(string fileName, int rowCount)
    {
        try
        {
            if (energyStore == null)
            {
                Log.Warning("db_EnergyLogManager", $"Cannot mark file as processed - DataStore is null for {fileName}");
                return;
            }

            var table = energyStore.Tables?.Get<Table>(TRACKING_TABLE_NAME);
            if (table == null)
            {
                Log.Warning("db_EnergyLogManager", $"Cannot mark file as processed - Table '{TRACKING_TABLE_NAME}' not found for {fileName}");
                return;
            }

            var cols = new[] { "DeviceName", "FileName", "ProcessedTimestamp", "RowCount" };
            var vals = new object[,] { { deviceName, fileName, DateTime.Now, rowCount } };
            table.Insert(cols, vals);
            Log.Debug("db_EnergyLogManager", $"Marked file as fully processed: {fileName}");
        }
        catch (Exception ex)
        {
            Log.Warning("db_EnergyLogManager", $"Failed to mark file as processed: {ex.Message}");
        }
    }

    private List<string> GetEnergyLogFilesFromPortal()
    {
        var files = new List<string>();

        try
        {
            // Download the HTML portal page
            var response = httpClient.GetAsync(portalUrl).Result;
            if (!response.IsSuccessStatusCode)
            {
                Log.Error("db_EnergyLogManager", $"Failed to fetch portal page: {response.StatusCode}");
                return files;
            }

            string htmlContent = response.Content.ReadAsStringAsync().Result;

            // Parse HTML to extract Energylog filenames (case-sensitive match)
            // Pattern: <a href="javascript:getlogresult('../LoggingResults/Energylog_20240416_121500_17.csv ');">Energylog_20240416_121500_17.csv</a>
            string pattern = @"Energylog_\d{8}_\d{6}_\d{2}\.csv";
            MatchCollection matches = Regex.Matches(htmlContent, pattern);

            foreach (Match match in matches)
            {
                string fileName = match.Value.Trim();
                if (!files.Contains(fileName))
                {
                    files.Add(fileName);
                }
            }

            files = files.OrderBy(f => f).ToList(); // Sort oldest first
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Failed to get file list from portal: {ex.Message}");
        }

        return files;
    }

    private void IngestAllExistingLogs()
    {
        Log.Warning("db_EnergyLogManager", "IngestAllExistingLogs called but is deprecated. Use BackfillHistoricalFiles instead.");
    }

    /// <summary>
    /// BACKGROUND BACKFILL: Process all historical files oldest→newest.
    /// Runs independently of periodic task. Pauses when device is offline and resumes when back.
    /// </summary>
    private void BackfillHistoricalFiles(LongRunningTask task)
    {
        backfillRunning = true;

        try
        {
            // Wait for device to be ready before starting backfill
            while (!IsDeviceReady())
            {
                statusInitial.Value = "Backfill: Waiting for device...";
                Thread.Sleep(DEVICE_OFFLINE_RETRY_MS);
                if (task.IsCancellationRequested) return;
            }

            // Ensure login
            if (!loginChecked)
            {
                statusInitial.Value = "Backfill: Logging in...";
                bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
                loginChecked = true;
                if (!isLoggedIn)
                {
                    statusInitial.Value = "Backfill: Login failed";
                    Log.Warning($"EnergyLogManager_HTTP-{deviceName}", "Backfill login failed, will rely on periodic task");
                    return;
                }
            }

            var allFiles = GetEnergyLogFilesFromPortal();

            if (allFiles.Count == 0)
            {
                statusInitial.Value = "Backfill: No files found";
                return;
            }

            // Exclude the most recent file — periodic task handles it
            string mostRecentFile = allFiles[allFiles.Count - 1];
            var filesToBackfill = allFiles.Where(f => f != mostRecentFile).ToList();

            Log.Debug($"EnergyLogManager_HTTP-{deviceName}", $"[BACKFILL] Starting. Total files: {filesToBackfill.Count} (excluding active file: {mostRecentFile})");

            int processedCount = 0;
            int skippedCount = 0;
            int totalRecordsProcessed = 0;
            int totalFiles = filesToBackfill.Count;

            foreach (var fileName in filesToBackfill)
            {
                if (task.IsCancellationRequested) break;

                // Skip files already fully processed
                if (IsFileFullyProcessed(fileName))
                {
                    skippedCount++;
                    continue;
                }

                // Wait for device to be ready (handles device on/off during backfill)
                int offlineWaitCount = 0;
                while (!IsDeviceReady())
                {
                    offlineWaitCount++;
                    statusInitial.Value = $"Backfill: Device offline, waiting... (retry {offlineWaitCount})";
                    Thread.Sleep(DEVICE_OFFLINE_RETRY_MS);
                    if (task.IsCancellationRequested) return;
                }

                // Re-login if we were offline for a while (session may have expired)
                if (offlineWaitCount > 0)
                {
                    loginManager.EnsureLoggedIn().Wait();
                }

                processedCount++;

                try
                {
                    statusInitial.Value = $"Backfill: {processedCount}/{totalFiles - skippedCount}: {fileName.Substring(0, Math.Min(25, fileName.Length))}...";
                    
                    int newRecords = ProcessEnergyLogFile(fileName, DateTime.MinValue);
                    totalRecordsProcessed += newRecords;
                    
                    MarkFileAsFullyProcessed(fileName, newRecords);
                    
                    // Delay between downloads to avoid overwhelming PM5000
                    Thread.Sleep(INITIAL_LOAD_DELAY_MS);
                }
                catch (Exception ex)
                {
                    Log.Warning("db_EnergyLogManager", $"[BACKFILL] Skipping {fileName}: {ex.Message}");
                }
            }

            statusInitial.Value = $"Backfill: Complete ({totalRecordsProcessed} records from {processedCount} files)";
            Log.Debug("db_EnergyLogManager", $"[BACKFILL] Complete. Processed: {processedCount}, Skipped: {skippedCount}, Records: {totalRecordsProcessed}");
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Backfill error: {ex.Message}");
            statusInitial.Value = $"Backfill: Error: {ex.Message}";
        }
        finally
        {
            backfillRunning = false;
        }
    }

    private void DownloadAndStoreNewEnergyLogs()
    {
        try
        {
            // Check if energy logging is enabled
            if (enableEnergyLogging == null || !enableEnergyLogging.Value)
            {
                Log.Debug("db_EnergyLogManager", "Energy logging is disabled");
                statusPeriodic.Value = "Periodic: Disabled";
                return;
            }

            // Check device readiness (independent check each cycle)
            if (!IsDeviceReady())
            {
                statusPeriodic.Value = "Periodic: Device Not Ready";
                statusLastUpdate.Value = $"Last Check: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")} (Device offline)";
                return; // Skip this cycle, will check again next time
            }

            // If device just came online and login wasn't completed, attempt login
            if (!loginChecked)
            {
                statusPeriodic.Value = "Periodic: Logging in...";
                
                bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
                loginChecked = true;
                
                if (!isLoggedIn)
                {
                    Log.Warning($"EnergyLogManager_HTTP-{deviceName}", "Login failed during periodic check");
                    statusPeriodic.Value = "Periodic: Login failed";
                    return;
                }
            }

            // Update status: checking (device is ready at this point)
            string backfillInfo = backfillRunning ? " [backfill active]" : "";
            statusPeriodic.Value = $"Periodic: Checking files...{backfillInfo}";

            var allFiles = GetEnergyLogFilesFromPortal();

            if (allFiles.Count == 0)
            {
                Log.Warning("db_EnergyLogManager", "[PERIODIC] No energy log files found");
                statusPeriodic.Value = "Periodic: No files";
                return;
            }

            // Check the last N files (handles file rollover scenarios)
            var recentFiles = allFiles.Skip(Math.Max(0, allFiles.Count - FILES_TO_CHECK_PERIODIC)).ToList();
            string mostRecentFile = allFiles.Count > 0 ? allFiles[allFiles.Count - 1] : null;

            DateTime lastProcessed = GetLastProcessedTimestamp();
            int totalNewRecords = 0;

            foreach (var fileName in recentFiles)
            {
                // Always re-read the most recent file (PM5000 appends to it continuously)
                // Skip older files only if they're marked as fully processed
                if (IsFileFullyProcessed(fileName) && fileName != mostRecentFile)
                {
                    continue;
                }

                try
                {
                    int newRecords = ProcessEnergyLogFile(fileName, lastProcessed);
                    totalNewRecords += newRecords;
                    
                    // Only mark as processed if NOT the most recent file (PM5000 appends to current file)
                    // Older closed files can be marked complete to avoid re-reading them
                    if (newRecords == 0 && fileName != mostRecentFile)
                    {
                        MarkFileAsFullyProcessed(fileName, 0);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("db_EnergyLogManager", $"[PERIODIC] Skipping {fileName}: {ex.Message}");
                }
            }

            // Update status and last update time
            lastPeriodicUpdateTime = DateTime.Now;
            if (totalNewRecords > 0)
            {
                statusPeriodic.Value = $"Periodic: Updated (+{totalNewRecords}){backfillInfo}";
            }
            else
            {
                statusPeriodic.Value = $"Periodic: Idle{backfillInfo}";
            }
            
            // Update last update timestamp
            statusLastUpdate.Value = $"Last Check: {lastPeriodicUpdateTime.ToString("yyyy-MM-dd HH:mm:ss")}";
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Failed to download energy logs: {ex.Message}");
            statusPeriodic.Value = "Periodic: Error";
        }
    }

    private string DownloadFileWithRetry(string fileUrl, string fileName)
    {
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
        {
            try
            {
                // Use ResponseHeadersRead so the timeout applies only to getting the headers,
                // then read the body stream separately with its own tolerance
                var response = httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("db_EnergyLogManager", $"Failed to download {fileName}: {response.StatusCode} (attempt {attempt}/{MAX_RETRIES})");
                    if (attempt < MAX_RETRIES)
                    {
                        int delayMs = 2000 * attempt; // 2s, 4s, 6s backoff
                        Thread.Sleep(delayMs);
                        continue;
                    }
                    return null;
                }

                string csvContent = response.Content.ReadAsStringAsync().Result;
                return csvContent;
            }
            catch (Exception ex)
            {
                Log.Warning("db_EnergyLogManager", $"Download attempt {attempt}/{MAX_RETRIES} failed for {fileName}: {ex.InnerException?.Message ?? ex.Message}");
                if (attempt < MAX_RETRIES)
                {
                    int delayMs = 3000 * attempt; // 3s, 6s, 9s backoff
                    Thread.Sleep(delayMs);
                }
            }
        }
        return null;
    }

    private int ProcessEnergyLogFile(string fileName, DateTime lastProcessed)
    {
        int recordsProcessed = 0;

        try
        {
            string fileUrl = $"{baseHttpUrl}/{fileName}";
            string csvContent = DownloadFileWithRetry(fileUrl, fileName);

            if (string.IsNullOrEmpty(csvContent))
            {
                Log.Warning("db_EnergyLogManager", $"Skipping {fileName}: failed to download after {MAX_RETRIES} retries (file may be corrupt on device)");
                return 0;
            }
            var lines = csvContent.Split('\n');

            if (lines.Length < 2)
                return 0; // Empty file or header only

            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var row = line.Split(',');
                if (row.Length < 34) continue;

                // CSV Columns: Record_ID, Year, Month_Day, Hour_Minute, Sec_MilliSec, ...
                string timestampStr = FormatTimestamp(row[1], row[2], row[3], row[4]);
                DateTime recordTime;

                if (!DateTime.TryParse(timestampStr, out recordTime))
                {
                    Log.Warning("db_EnergyLogManager", $"Invalid timestamp in file {fileName}: {timestampStr}");
                    continue;
                }

                if (recordTime <= lastProcessed)
                    continue; // Skip already processed records

                InsertEnergyRecord(row, recordTime, fileName);
                recordsProcessed++;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("db_EnergyLogManager", $"Skipping file {fileName}: {ex.Message}");
        }

        return recordsProcessed;
    }

    private void InsertEnergyRecord(string[] row, DateTime recordTime, string sourceFile)
    {
        try
        {
            if (row.Length < 34)
            {
                Log.Warning("db_EnergyLogManager", $"Row has only {row.Length} columns, expected 34. Skipping.");
                return;
            }

            // Check for duplicate before inserting
            string checkQuery = $"SELECT COUNT(*) FROM {TABLE_NAME} WHERE DeviceName = '{deviceName}' AND Timestamp = '{recordTime:yyyy-MM-dd HH:mm:ss.fff}'";
            energyStore.Query(checkQuery, out _, out object[,] checkResult);
            if (checkResult != null && checkResult.GetLength(0) > 0 && Convert.ToInt32(checkResult[0, 0]) > 0)
            {
                return; // Record already exists
            }

            string[] columns = {
                "DeviceName", "Timestamp", "RecordID",
                "Status_1_Count_xM", "Status_1_Count_x1",
                "Status_2_Count_xM", "Status_2_Count_x1",
                "Status_3_Count_xM", "Status_3_Count_x1",
                "Status_4_Count_xM", "Status_4_Count_x1",
                "GWh_Fwd", "kWh_Fwd",
                "GWh_Rev", "kWh_Rev",
                "GWh_Net", "kWh_Net",
                "GVARh_Fwd", "kVARh_Fwd",
                "GVARh_Rev", "kVARh_Rev",
                "GVARh_Net", "kVARh_Net",
                "GVAh", "kVAh",
                "kW_Demand", "kVAR_Demand", "kVA_Demand", "Demand_PF",
                "Projected_kW_Demand", "Projected_kVAR_Demand", "Projected_kVA_Demand",
                "Total_kWh_Fwd", "Total_kWh_Rev", "Total_kWh_Net",
                "Total_kVARh_Fwd", "Total_kVARh_Rev", "Total_kVARh_Net",
                "Total_kVAh",
                "SourceFileName", "CreatedAt"
            };

            object[,] values = new object[1, columns.Length];

            // Metadata
            values[0, 0] = deviceName;
            values[0, 1] = recordTime;
            values[0, 2] = ParseInt(row[0]);

            // Status counts
            values[0, 3] = ParseInt(row[5]);
            values[0, 4] = ParseInt(row[6]);
            values[0, 5] = ParseInt(row[7]);
            values[0, 6] = ParseInt(row[8]);
            values[0, 7] = ParseInt(row[9]);
            values[0, 8] = ParseInt(row[10]);
            values[0, 9] = ParseInt(row[11]);
            values[0, 10] = ParseInt(row[12]);

            // Energy values
            double gWhFwd = ParseDouble(row[13]);
            double kWhFwd = ParseDouble(row[14]);
            values[0, 11] = gWhFwd;
            values[0, 12] = kWhFwd;

            double gWhRev = ParseDouble(row[15]);
            double kWhRev = ParseDouble(row[16]);
            values[0, 13] = gWhRev;
            values[0, 14] = kWhRev;

            double gWhNet = ParseDouble(row[17]);
            double kWhNet = ParseDouble(row[18]);
            values[0, 15] = gWhNet;
            values[0, 16] = kWhNet;

            double gVARhFwd = ParseDouble(row[19]);
            double kVARhFwd = ParseDouble(row[20]);
            values[0, 17] = gVARhFwd;
            values[0, 18] = kVARhFwd;

            double gVARhRev = ParseDouble(row[21]);
            double kVARhRev = ParseDouble(row[22]);
            values[0, 19] = gVARhRev;
            values[0, 20] = kVARhRev;

            double gVARhNet = ParseDouble(row[23]);
            double kVARhNet = ParseDouble(row[24]);
            values[0, 21] = gVARhNet;
            values[0, 22] = kVARhNet;

            double gVAh = ParseDouble(row[25]);
            double kVAh = ParseDouble(row[26]);
            values[0, 23] = gVAh;
            values[0, 24] = kVAh;

            // Demand values
            values[0, 25] = ParseDouble(row[27]);
            values[0, 26] = ParseDouble(row[28]);
            values[0, 27] = ParseDouble(row[29]);
            values[0, 28] = ParseDouble(row[30]);

            // Projected Demand
            values[0, 29] = ParseDouble(row[31]);
            values[0, 30] = ParseDouble(row[32]);
            values[0, 31] = ParseDouble(row[33]);

            // Calculated totals
            values[0, 32] = gWhFwd * 1000000 + kWhFwd;
            values[0, 33] = gWhRev * 1000000 + kWhRev;
            values[0, 34] = gWhNet * 1000000 + kWhNet;
            values[0, 35] = gVARhFwd * 1000000 + kVARhFwd;
            values[0, 36] = gVARhRev * 1000000 + kVARhRev;
            values[0, 37] = gVARhNet * 1000000 + kVARhNet;
            values[0, 38] = gVAh * 1000000 + kVAh;

            values[0, 39] = sourceFile;
            values[0, 40] = DateTime.Now;

            energyStore.Tables.Get<Table>(TABLE_NAME).Insert(columns, values);
        }
        catch (Exception ex)
        {
            Log.Error("db_EnergyLogManager", $"Insert failed: {ex.Message}");
        }
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

    private int ParseInt(string value)
    {
        return int.TryParse(value?.Trim(), out int result) ? result : 0;
    }

    private double ParseDouble(string value)
    {
        return double.TryParse(value?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double result) ? result : 0.0;
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

    private int GetTotalRecordCount()
    {
        try
        {
            string query = $"SELECT COUNT(*) FROM {TABLE_NAME} WHERE DeviceName = '{deviceName}'";
            energyStore.Query(query, out _, out object[,] results);
            if (results != null && results.GetLength(0) > 0)
                return Convert.ToInt32(results[0, 0]);
        }
        catch { }
        return 0;
    }


    [ExportMethod]
    public void DropUnifiedTable()
    {
        
           
            var myStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            
            
                myStore.RemoveTable(TABLE_NAME);
                myStore.RemoveTable(TRACKING_TABLE_NAME);
                
               
                Log.Info($"EnergyLogManager-{deviceName}", $"Unified table '{TABLE_NAME}' dropped successfully.");
            
        
    }
}
