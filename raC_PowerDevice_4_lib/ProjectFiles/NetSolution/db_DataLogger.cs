#region Using directives
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using DeviceLogin;
using System.Net;
using System.Threading;
using System.Net.Http;
using Meta.Numerics.Statistics;
using System.Text.RegularExpressions;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;

#endregion

public class db_DataLogger : BaseNetLogic
{
    private const string UNIFIED_TABLE_NAME = "DataLogger_AllDevices";
    private const string TRACKING_TABLE_NAME = "DataLogger_ProcessedFiles";
    private const int FILES_TO_CHECK_PERIODIC = 3; // Check last 3 files for rollover scenarios
    private const int MAX_RETRIES = 3;               // Retry count for failed HTTP downloads
    private const int INITIAL_LOAD_DELAY_MS = 3000;  // 3 seconds delay between files during initial load
    private const int DEVICE_OFFLINE_RETRY_MS = 30000; // 30 seconds wait when device is offline during backfill
    
    private string deviceName;
    private string ipAddress;
    private string baseHttpUrl;
    private string portalUrl;
    private HttpClient httpClient;
    private IUAVariable logLabel;
    private IUAVariable isBusy;
    private IUAVariable enableDataLogging;
    private IUAVariable statusInitial;      // UI: First-run progress (e.g., "Processing file 5/50")
    private IUAVariable statusPeriodic;     // UI: Periodic status (e.g., "Checking..." or "Idle")
    private IUAVariable statusLastUpdate;   // UI: Last update timestamp
    private Store dataStore;
    private DeviceLoginManager loginManager;
    private PeriodicTask periodicTask;
    private LongRunningTask startupTask;
    private LongRunningTask backfillTask;
    private string remoteDir = "/LoggingResults";
    private bool schemaInitialized = false;
    private bool initialLoadComplete = false;
    private bool loginChecked = false;
    private volatile bool backfillRunning = false;   // Tracks if background backfill is active (informational only, does NOT block periodic)
    private DateTime lastPeriodicUpdateTime = DateTime.MinValue;
    private DateTime lastDeviceReadyCheck = DateTime.MinValue;
    private bool lastDeviceReadyStatus = false;
    private readonly object lockObject = new object();

    public override void Start()
    {
        // Identify device
        deviceName = Owner?.BrowseName ?? "UnknownDevice";

        // Read IP address
        var ipVar = Owner.GetVariable("Val_IPAddress");
        if (ipVar == null || string.IsNullOrWhiteSpace(ipVar.Value))
        {
            SetStatus("Error: IP address missing");
            return;
        }
        ipAddress = ipVar.Value;

        // Optional remote dir override
        var remoteDirVar = Owner.GetVariable("Val_RemoteCsvDir");
        if (remoteDirVar != null && !string.IsNullOrWhiteSpace(remoteDirVar.Value))
            remoteDir = remoteDirVar.Value;

        // Status label variable (create if missing)
        logLabel = LogicObject.GetVariable("logLabel") ?? InformationModel.MakeVariable("logLabel", OpcUa.DataTypes.String);
        if (LogicObject.GetVariable("logLabel") == null) LogicObject.Add(logLabel);

        // Busy flag for UI overlay
        isBusy = LogicObject.GetVariable("IsBusy") ?? InformationModel.MakeVariable("IsBusy", OpcUa.DataTypes.Boolean);
        if (LogicObject.GetVariable("IsBusy") == null) LogicObject.Add(isBusy);
        isBusy.Value = true;

        // Create or get status UI variables
        statusInitial = Owner.GetVariable("Val_DataLog_InitStatus");
        if (statusInitial == null)
        {
            statusInitial = InformationModel.MakeVariable("Val_DataLog_InitStatus", OpcUa.DataTypes.String);
            statusInitial.Value = "Init Status: Initializing...";
            Owner.Add(statusInitial);
        }

        statusPeriodic = Owner.GetVariable("Val_DataLog_PeriodicStatus");
        if (statusPeriodic == null)
        {
            statusPeriodic = InformationModel.MakeVariable("Val_DataLog_PeriodicStatus", OpcUa.DataTypes.String);
            statusPeriodic.Value = "Periodic: Idle";
            Owner.Add(statusPeriodic);
        }

        statusLastUpdate = Owner.GetVariable("Val_DataLog_LastUpdate");
        if (statusLastUpdate == null)
        {
            statusLastUpdate = InformationModel.MakeVariable("Val_DataLog_LastUpdate", OpcUa.DataTypes.String);
            statusLastUpdate.Value = "Last Update: Never";
            Owner.Add(statusLastUpdate);
        }

        // Get DataStore alias
        var storeAliasParent = Project.Current
            .GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores");
        var storeAlias = storeAliasParent?.Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
        if (storeAlias == null)
        {
            SetStatus("Error: DataStore alias missing");
            isBusy.Value = false;
            return;
        }
        dataStore = storeAlias;

        // Initialize DeviceLoginManager
        loginManager = new DeviceLoginManager(ipAddress, Owner, deviceName);

        // Initialize HTTP client and URLs
        baseHttpUrl = $"http://{ipAddress}/LoggingResults";
        portalUrl = $"http://{ipAddress}/logresults/Data_Log.shtm";
        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(120); // 2 minutes - PM5000 embedded web server can be slow for large CSV files

        // Read enable/disable logging variable
        enableDataLogging = Owner.GetVariable("Set_EnableDataLogLogging");
        if (enableDataLogging == null)
        {
            Log.Warning($"db_DataLogger-{deviceName}", "Set_EnableDataLogLogging variable not found, defaulting to enabled");
            enableDataLogging = InformationModel.MakeVariable("Set_EnableDataLogLogging", OpcUa.DataTypes.Boolean);
            enableDataLogging.Value = true;
            Owner.Add(enableDataLogging);
        }

        // Subscribe to enable/disable changes
        enableDataLogging.VariableChange += EnableDataLogging_VariableChange;

        if (!enableDataLogging.Value)
        {
            statusInitial.Value = "Init Status: Data Logging Is Disabled";
            statusPeriodic.Value = "Periodic: Data Logging Is Disabled";
            statusLastUpdate.Value = "Last Update: N/A";
            isBusy.Value = false;
            return;
        }

        // Kick off heavy initialization in a LongRunningTask
        startupTask = new LongRunningTask(TaskInitializeAndDownload, LogicObject);
        startupTask.Start();
    }

    public override void Stop()
    {
        startupTask?.Dispose();
        startupTask = null;

        backfillTask?.Dispose();
        backfillTask = null;

        periodicTask?.Cancel();
        periodicTask?.Dispose();
        periodicTask = null;

        if (enableDataLogging != null)
            enableDataLogging.VariableChange -= EnableDataLogging_VariableChange;

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
                    Log.Info($"db_DataLogger_HTTP-{deviceName}", "Device is now Ready");
                else
                    Log.Warning($"db_DataLogger_HTTP-{deviceName}", "Device is Not Ready (HTTP check failed)");

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
                Log.Warning($"db_DataLogger_HTTP-{deviceName}", $"Device communication failed: {ex.Message}");
                lastDeviceReadyStatus = false;
            }

            lastDeviceReadyCheck = DateTime.Now;
            return false;
        }
    }

    private void EnableDataLogging_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            if (periodicTask == null)
            {
                // Update status labels to show enabled state
                if (statusInitial != null) statusInitial.Value = "Init Status: Initializing...";
                if (statusPeriodic != null) statusPeriodic.Value = "Periodic: Waiting for init...";
                
                // Run full initialization (creates tables, starts periodic, starts backfill)
                startupTask = new LongRunningTask(TaskInitializeAndDownload, LogicObject);
                startupTask.Start();
            }
        }
        else
        {
            if (periodicTask != null)
            {
                periodicTask.Cancel();
                periodicTask.Dispose();
                periodicTask = null;
            }
            if (backfillTask != null)
            {
                backfillTask.Dispose();
                backfillTask = null;
            }
            // Update status labels to show disabled state
            if (statusInitial != null) statusInitial.Value = "Init Status: Data Logging Is Disabled";
            if (statusPeriodic != null) statusPeriodic.Value = "Periodic: Data Logging Is Disabled";
            if (statusLastUpdate != null) statusLastUpdate.Value = "Last Update: N/A";
        }
    }

    private void TaskInitializeAndDownload(LongRunningTask task)
    {
        try
        {
            SetStatus("Initializing...");
            EnsureUnifiedTableExists();
            EnsureFileTrackingTableExists();
            schemaInitialized = true;

            // Check device readiness first (independent HTTP check)
            statusInitial.Value = "Init Status: Checking device...";
            if (!IsDeviceReady())
            {
                statusInitial.Value = "Init Status: Device Not Ready";
                Log.Warning($"db_DataLogger_HTTP-{deviceName}", "Device not ready at startup. Will retry in periodic checks.");

                // Start periodic task anyway — it will handle device readiness and login each cycle
                InitializePeriodicTask();
                return;
            }

            // Perform login check once at startup
            if (!loginChecked)
            {
                SetStatus("Logging in...");
                statusInitial.Value = "Init Status: Logging in...";
                bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
                loginChecked = true;
                
                if (!isLoggedIn)
                {
                    SetStatus("Cannot initialize: Login to device failed.");
                    statusInitial.Value = "Init Status: Login failed";
                    Log.Error($"db_DataLogger-{deviceName}", "Cannot initialize DataLogger due to login failure.");
                    
                    // Start periodic task anyway — login might succeed later
                    InitializePeriodicTask();
                    return;
                }
            }

            // === NEW ARCHITECTURE: Start periodic FIRST, then backfill in background ===
            // This ensures new data flows immediately, regardless of how many historical files exist
            
            // 1. Start periodic task IMMEDIATELY — always checks latest files every cycle
            InitializePeriodicTask();
            initialLoadComplete = true; // Periodic can run immediately
            SetStatus("Ready");
            
            // 2. Start background backfill for historical files (runs independently, doesn't block periodic)
            backfillTask = new LongRunningTask(BackfillHistoricalFiles, LogicObject);
            backfillTask.Start();
            Log.Debug($"db_DataLogger-{deviceName}", "Background backfill task started");
        }
        catch (Exception ex)
        {
            statusInitial.Value = $"Init Status: Error: {ex.Message}";
            SetStatus($"Init error: {ex.Message}");
            Log.Error($"db_DataLogger-{deviceName}", $"Init exception: {ex}");
        }
        finally
        {
            if (isBusy != null) isBusy.Value = false;
        }
    }

    /// <summary>
    /// FIRST-TIME LOAD: Deprecated — replaced by BackfillHistoricalFiles
    /// </summary>
    private void FirstTimeLoadAllFiles(LongRunningTask task)
    {
        Log.Warning("db_DataLogger", "FirstTimeLoadAllFiles called but is deprecated. Use BackfillHistoricalFiles instead.");
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
                    Log.Warning($"db_DataLogger-{deviceName}", "Backfill login failed, will rely on periodic task");
                    return;
                }
            }

            var allFiles = GetDataLogFilesFromPortal();

            if (allFiles.Count == 0)
            {
                statusInitial.Value = "Backfill: No files found";
                return;
            }

            // Exclude the most recent file — periodic task handles it
            string mostRecentFile = allFiles[allFiles.Count - 1];
            var filesToBackfill = allFiles.Where(f => f != mostRecentFile).ToList();

            Log.Debug($"db_DataLogger-{deviceName}", $"[BACKFILL] Starting. Total files: {filesToBackfill.Count} (excluding active file: {mostRecentFile})");

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
                    
                    int newRows = DownloadAndProcessDataLogFile(fileName);
                    totalRecordsProcessed += newRows;
                    
                    MarkFileAsFullyProcessed(fileName, newRows);
                    
                    // Delay between downloads to avoid overwhelming PM5000
                    Thread.Sleep(INITIAL_LOAD_DELAY_MS);
                }
                catch (Exception ex)
                {
                    Log.Warning("db_DataLogger", $"[BACKFILL] Skipping {fileName}: {ex.Message}");
                }
            }

            statusInitial.Value = $"Backfill: Complete ({totalRecordsProcessed} records from {processedCount} files)";
            Log.Debug("db_DataLogger", $"[BACKFILL] Complete. Processed: {processedCount}, Skipped: {skippedCount}, Records: {totalRecordsProcessed}");
        }
        catch (Exception ex)
        {
            Log.Error("db_DataLogger", $"Backfill error: {ex.Message}");
            statusInitial.Value = $"Backfill: Error: {ex.Message}";
        }
        finally
        {
            backfillRunning = false;
        }
    }

    private void InitializePeriodicTask()
    {
        try
        {
            int checkPeriodSeconds = ReadDurationVariable("Cfg_DataLogCheckPeriodSeconds", 900); // Default 15 min
            periodicTask = new PeriodicTask(DownloadAndStoreNewDataLogs, checkPeriodSeconds * 1000, LogicObject);
            periodicTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("db_DataLogger", $"Failed to start periodic task: {ex.Message}");
        }
    }

    /// <summary>
    /// PERIODIC TASK: Check the last N files for new records via HTTP (handles file rollover)
    /// </summary>
    private void DownloadAndStoreNewDataLogs()
    {
        try
        {
            // Check if data logging is enabled
            if (enableDataLogging == null || !enableDataLogging.Value)
            {
                statusPeriodic.Value = "Periodic: Disabled";
                return;
            }

            // Check device readiness (independent check each cycle)
            if (!IsDeviceReady())
            {
                statusPeriodic.Value = "Periodic: Device Not Ready";
                statusLastUpdate.Value = $"Last Check: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Device offline)";
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
                    Log.Warning($"db_DataLogger-{deviceName}", "Login failed during periodic check");
                    statusPeriodic.Value = "Periodic: Login failed";
                    return;
                }
            }

            // Update status: checking
            string backfillInfo = backfillRunning ? " [backfill active]" : "";
            statusPeriodic.Value = $"Periodic: Checking...{backfillInfo}";

            var allFiles = GetDataLogFilesFromPortal();

            if (allFiles.Count == 0)
            {
                Log.Warning("db_DataLogger", "[PERIODIC] No datalog files found");
                statusPeriodic.Value = "Periodic: No files";
                return;
            }

            // Check the last N files (handles file rollover scenarios)
            var recentFiles = allFiles.Skip(Math.Max(0, allFiles.Count - FILES_TO_CHECK_PERIODIC)).ToList();
            string mostRecentFile = allFiles.Count > 0 ? allFiles[allFiles.Count - 1] : null;

            DateTime lastProcessed = GetLatestTimestampForDevice(dataStore, deviceName);
            int totalNewRows = 0;

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
                    int newRows = DownloadAndProcessDataLogFile(fileName);
                    totalNewRows += newRows;
                    
                    // Only mark as processed if NOT the most recent file (PM5000 appends to current file)
                    // Older closed files can be marked complete to avoid re-reading them
                    if (newRows == 0 && fileName != mostRecentFile)
                    {
                        MarkFileAsFullyProcessed(fileName, 0);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("db_DataLogger", $"[PERIODIC] Skipping {fileName}: {ex.Message}");
                }
            }

            // Update status and last update time
            lastPeriodicUpdateTime = DateTime.Now;
            if (totalNewRows > 0)
            {
                statusPeriodic.Value = $"Periodic: Updated (+{totalNewRows}){backfillInfo}";
                SetStatus($"Updated: {totalNewRows} new rows");
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
            statusPeriodic.Value = "Periodic: Error";
            SetStatus($"Periodic check failed: {ex.Message}");
            Log.Error("db_DataLogger", $"Periodic check error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get list of DataLog files from HTTP portal
    /// </summary>
    private List<string> GetDataLogFilesFromPortal()
    {
        var files = new List<string>();

        try
        {
            // Download the HTML portal page
            var response = httpClient.GetAsync(portalUrl).Result;
            if (!response.IsSuccessStatusCode)
            {
                Log.Error("db_DataLogger", $"Failed to fetch portal page: {response.StatusCode}");
                return files;
            }

            string htmlContent = response.Content.ReadAsStringAsync().Result;

            // Parse HTML to extract Datalog filenames (case-insensitive match)
            // Pattern: <a href="javascript:getlogresult('../LoggingResults/Datalog_20240416_121500_17.csv ');">Datalog_20240416_121500_17.csv</a>
            string pattern = @"Datalog_\d{8}_\d{6}_\d{2}\.csv";
            MatchCollection matches = Regex.Matches(htmlContent, pattern, RegexOptions.IgnoreCase);

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
            Log.Error("db_DataLogger", $"Failed to get file list from portal: {ex.Message}");
        }

        return files;
    }

    /// <summary>
    /// Download file content with retry logic and exponential backoff
    /// </summary>
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
                    Log.Warning("db_DataLogger", $"Failed to download {fileName}: {response.StatusCode} (attempt {attempt}/{MAX_RETRIES})");
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
                Log.Warning("db_DataLogger", $"Download attempt {attempt}/{MAX_RETRIES} failed for {fileName}: {ex.InnerException?.Message ?? ex.Message}");
                if (attempt < MAX_RETRIES)
                {
                    int delayMs = 3000 * attempt; // 3s, 6s, 9s backoff
                    Thread.Sleep(delayMs);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Download and process a single DataLog file via HTTP
    /// </summary>
    private int DownloadAndProcessDataLogFile(string fileName)
    {
        int recordsProcessed = 0;

        try
        {
            string fileUrl = $"{baseHttpUrl}/{fileName}";
            string csvContent = DownloadFileWithRetry(fileUrl, fileName);

            if (string.IsNullOrEmpty(csvContent))
            {
                Log.Warning("db_DataLogger", $"Skipping {fileName}: failed to download after {MAX_RETRIES} retries (file may be corrupt on device)");
                return 0;
            }

            recordsProcessed = LogCsvContentToDatabase(csvContent, fileName);
        }
        catch (Exception ex)
        {
            Log.Warning("db_DataLogger", $"Skipping file {fileName}: {ex.Message}");
        }

        return recordsProcessed;
    }

    [ExportMethod]
    public void ExportDeviceDataReport()
    {
        try
        {
            var reportLines = new List<string>();
            reportLines.Add("DataLogger Device Report");
            reportLines.Add($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            reportLines.Add("-----------------------------------------------");

            dataStore.Query($"SELECT DISTINCT DeviceName FROM {UNIFIED_TABLE_NAME}", out _, out object[,] deviceResult);
            if (deviceResult == null || deviceResult.GetLength(0) == 0)
            {
                reportLines.Add("No data found.");
            }
            else
            {
                for (int i = 0; i < deviceResult.GetLength(0); i++)
                {
                    var dev = deviceResult[i, 0]?.ToString() ?? "Unknown";

                    dataStore.Query($"SELECT COUNT(*) FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{dev}'", out _, out object[,] countResult);
                    int rowCount = (countResult != null && countResult.GetLength(0) > 0) ? Convert.ToInt32(countResult[0, 0]) : 0;

                    dataStore.Query($"SELECT MAX(Timestamp) FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{dev}'", out _, out object[,] tsResult);
                    string latestTs = (tsResult != null && tsResult.GetLength(0) > 0 && tsResult[0, 0] != null && tsResult[0, 0] != DBNull.Value)
                        ? Convert.ToDateTime(tsResult[0, 0]).ToString("yyyy-MM-dd HH:mm:ss")
                        : "N/A";

                    dataStore.Query($"SELECT COUNT(DISTINCT SourceFile) FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{dev}'", out _, out object[,] fileResult);
                    int fileCount = (fileResult != null && fileResult.GetLength(0) > 0) ? Convert.ToInt32(fileResult[0, 0]) : 0;

                    reportLines.Add($"Device: {dev}");
                    reportLines.Add($"  Rows: {rowCount}");
                    reportLines.Add($"  Files Processed: {fileCount}");
                    reportLines.Add($"  Latest Timestamp: {latestTs}");
                    reportLines.Add("");
                }
            }

            var baseDir = new FTOptix.Core.ResourceUri("%PROJECTDIR%").Uri;
            var outDir = Path.Combine(baseDir, "Downloads");
            var outFile = Path.Combine(outDir, "DataLogger_Report.txt");
            Directory.CreateDirectory(outDir);
            File.WriteAllLines(outFile, reportLines);
            if (logLabel != null) logLabel.Value = $"Report generated: {outFile}";
        }
        catch (Exception ex)
        {
            Log.Error("db_DataLogger", $"Export error: {ex.Message}");
            if (ex.InnerException != null)
                Log.Error("db_DataLogger", $"Export InnerException: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
        }
    }

    private int LogCsvContentToDatabase(string csvContent, string sourceFileName)
    {
        try
        {
            if (dataStore == null) { SetStatus("Error: DataStore not available"); return 0; }

            var lines = csvContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) { SetStatus("No rows parsed from CSV"); return 0; }

            var headers = lines[0].Split(',').Select(h => h.Trim()).Where(h => !string.IsNullOrEmpty(h)).ToList();

            var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++) headerIndex[headers[i]] = i;

            int yearIndex = FindHeaderIndex(headers, "Year");
            int mdIndex   = FindHeaderIndex(headers, "Month_Day", "MonthDay");
            int hmIndex   = FindHeaderIndex(headers, "Hour_Minute", "HourMinute");
            int smIndex   = FindHeaderIndex(headers, "Sec_MilliSec", "SecMilliSec", "SecondMilli");
            int tsIndex = FindHeaderIndex(headers, "Timestamp");

            var excludeCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Year", "Month_Day", "MonthDay",
                "Hour_Minute", "HourMinute",
                "Sec_MilliSec", "SecMilliSec", "SecondMilli",
                "Timestamp", "SourceFile"
            };

            var filteredHeaders = headers.Where(h => !excludeCols.Contains(h)).ToList();
            EnsureColumnsExist(filteredHeaders);

            var insertColumns = new List<string> { "DeviceName", "Timestamp" };
            insertColumns.AddRange(filteredHeaders);
            insertColumns.Add("SourceFile");

            var table = dataStore.Tables.Get<Table>(UNIFIED_TABLE_NAME);
            var allValues = new List<object[]>();

            DateTime latestInDb = GetLatestTimestampForDevice(dataStore, deviceName);

            foreach (var line in lines.Skip(1))
            {
                var values = line.Split(',');

                DateTime ts = DateTime.Now;
                try
                {
                    bool haveSplit =
                        yearIndex >= 0 && mdIndex >= 0 && hmIndex >= 0 && smIndex >= 0 &&
                        yearIndex < values.Length && mdIndex < values.Length &&
                        hmIndex < values.Length && smIndex < values.Length;

                    if (haveSplit)
                    {
                        string formatted = FormatTimestamp(values[yearIndex], values[mdIndex], values[hmIndex], values[smIndex]);
                        ts = DateTime.ParseExact(formatted, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    }
                    else if (tsIndex >= 0 && tsIndex < values.Length)
                    {
                        DateTime parsedTs;
                        if (DateTime.TryParse(values[tsIndex], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsedTs))
                            ts = parsedTs;
                    }
                }
                catch
                {
                    // Ignore parse errors
                }

                if (ts <= latestInDb) continue;

                var row = new List<object>(insertColumns.Count) { deviceName, ts };

                foreach (var h in filteredHeaders)
                {
                    int idx;
                    if (headerIndex.TryGetValue(h, out idx) && idx < values.Length)
                        row.Add(values[idx]);
                    else
                        row.Add(null);
                }

                row.Add(sourceFileName);
                allValues.Add(row.ToArray());
            }

            if (allValues.Count == 0) return 0;

            var rawValues = new object[allValues.Count, insertColumns.Count];
            for (int r = 0; r < allValues.Count; r++)
                for (int c = 0; c < insertColumns.Count; c++)
                    rawValues[r, c] = allValues[r][c];

            table.Insert(insertColumns.ToArray(), rawValues);
            return allValues.Count;
        }
        catch (Exception ex)
        {
            SetStatus($"Log error: {ex.Message}");
            Log.Error($"db_DataLogger-{deviceName}", $"LogCsvContentToDatabase error: {ex.Message}");
            return 0;
        }
    }

    private void SetStatus(string text) { if (logLabel != null) logLabel.Value = text; }

    private void EnsureUnifiedTableExists()
    {
        if (!TableExists(dataStore, UNIFIED_TABLE_NAME))
        {
            dataStore.AddTable(UNIFIED_TABLE_NAME);
            dataStore.AddColumn(UNIFIED_TABLE_NAME, "DeviceName", OpcUa.DataTypes.String);
            dataStore.AddColumn(UNIFIED_TABLE_NAME, "Timestamp", OpcUa.DataTypes.DateTime);
            dataStore.AddColumn(UNIFIED_TABLE_NAME, "SourceFile", OpcUa.DataTypes.String);
            Log.Info($"db_DataLogger-{deviceName}", $"Created table: {UNIFIED_TABLE_NAME}");
        }
    }

    private void EnsureFileTrackingTableExists()
    {
        if (!TableExists(dataStore, TRACKING_TABLE_NAME))
        {
            dataStore.AddTable(TRACKING_TABLE_NAME);
            dataStore.AddColumn(TRACKING_TABLE_NAME, "DeviceName", OpcUa.DataTypes.String);
            dataStore.AddColumn(TRACKING_TABLE_NAME, "FileName", OpcUa.DataTypes.String);
            dataStore.AddColumn(TRACKING_TABLE_NAME, "ProcessedTimestamp", OpcUa.DataTypes.DateTime);
            dataStore.AddColumn(TRACKING_TABLE_NAME, "RowCount", OpcUa.DataTypes.Int32);
            Log.Info($"db_DataLogger-{deviceName}", $"Created tracking table: {TRACKING_TABLE_NAME}");
        }
    }

    private bool TableExists(Store store, string tableName)
    {
        try
        {
            store.Query($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'", out _, out object[,] resultSet);
            return resultSet != null && resultSet.GetLength(0) > 0 && Convert.ToInt32(resultSet[0, 0]) > 0;
        }
        catch
        {
            return false;
        }
    }

    private HashSet<string> GetExistingColumns(Store store, string tableName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            store.Query($"SELECT * FROM {tableName} LIMIT 1", out string[] colNames, out object[,] resultSet);
            if (colNames != null)
            {
                foreach (var col in colNames) set.Add(col);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"db_DataLogger-{deviceName}", $"GetExistingColumns error: {ex.Message}");
        }
        return set;
    }

    private void EnsureColumnsExist(List<string> headers)
    {
        var existing = GetExistingColumns(dataStore, UNIFIED_TABLE_NAME);
        foreach (var header in headers)
        {
            if (!existing.Contains(header))
            {
                try
                {
                    dataStore.AddColumn(UNIFIED_TABLE_NAME, header, OpcUa.DataTypes.String);
                }
                catch (Exception ex)
                {
                    Log.Warning($"db_DataLogger-{deviceName}", $"Failed to add column {header}: {ex.Message}");
                }
            }
        }
    }

    private DateTime GetLatestTimestampForDevice(Store store, string devName)
    {
        try
        {
            store.Query($"SELECT MAX(Timestamp) FROM {UNIFIED_TABLE_NAME} WHERE DeviceName = '{devName}'", out _, out object[,] resultSet);
            return (resultSet != null && resultSet.GetLength(0) > 0 && resultSet[0, 0] != null && resultSet[0, 0] != DBNull.Value)
                ? Convert.ToDateTime(resultSet[0, 0])
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private bool IsFileFullyProcessed(string fileName)
    {
        try
        {
            var query = $"SELECT COUNT(*) FROM {TRACKING_TABLE_NAME} " +
                       $"WHERE DeviceName = '{deviceName}' AND FileName = '{fileName}'";
            dataStore.Query(query, out _, out object[,] result);
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
            if (dataStore == null)
            {
                Log.Warning("db_DataLogger", $"Cannot mark file as processed - DataStore is null for {fileName}");
                return;
            }

            var table = dataStore.Tables?.Get<Table>(TRACKING_TABLE_NAME);
            if (table == null)
            {
                Log.Warning("db_DataLogger", $"Cannot mark file as processed - Table '{TRACKING_TABLE_NAME}' not found for {fileName}");
                return;
            }

            var cols = new[] { "DeviceName", "FileName", "ProcessedTimestamp", "RowCount" };
            var vals = new object[,] { { deviceName, fileName, DateTime.Now, rowCount } };
            table.Insert(cols, vals);
        }
        catch (Exception ex)
        {
            Log.Warning("db_DataLogger", $"Failed to mark file as processed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates DataLog file naming convention: DataLog_YYYYMMDD_hhmm_HH.csv
    /// Example: DataLog_20251217_1149_08.csv
    /// </summary>
    private bool IsValidDataLogFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        // Pattern: DataLog_YYYYMMDD_hhmm_HH.csv
        // Y=year, M=month, D=day, h=hour, m=minute, H=UTC hour
        var pattern = @"^Datalog_\d{8}_\d{6}_\d{2}\.csv$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Timestamp formatter (EnergyLog-style)
    /// Inputs:
    ///  - year:       "2025"
    ///  - monthDay:   "1217"    (MMDD)
    ///  - hourMinute: "1149"    (HHmm)
    ///  - secMilliSec:"082" or "821"    (ssfff)
    /// Output: "2025-12-17 11:49:00.082"
    /// </summary>
    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            // Month_Day -> MM/DD
            int md = int.Parse((monthDay ?? "0").Trim());
            string month = (md / 100).ToString().PadLeft(2, '0');
            string day   = (md % 100).ToString().PadLeft(2, '0');

            // Hour_Minute -> HH:mm
            int hm = int.Parse((hourMinute ?? "0").Trim());
            string hour   = (hm / 100).ToString().PadLeft(2, '0');
            string minute = (hm % 100).ToString().PadLeft(2, '0');

            // Sec_MilliSec -> ss.fff
            int sm = int.Parse((secMilliSec ?? "0").Trim());
            string seconds      = (sm / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (sm % 1000).ToString().PadLeft(3, '0');

            return $"{year}-{month}-{day} {hour}:{minute}:{seconds}.{milliseconds}";
        }
        catch
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Find header index with alias support (case-insensitive)
    /// </summary>
    private int FindHeaderIndex(List<string> headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string hUpper = headers[i].ToUpperInvariant();
            foreach (var name in candidates)
            {
                if (hUpper == (name ?? "").ToUpperInvariant())
                    return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Reads a Duration variable (in seconds) from Owner object with a default fallback
    /// </summary>
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
            Log.Warning($"db_DataLogger-{deviceName}", $"{variableName} not found. Using default: {defaultValueSeconds} seconds");
            return defaultValueSeconds;
        }
        catch (Exception ex)
        {
            Log.Error($"db_DataLogger-{deviceName}", $"Error reading {variableName}: {ex.Message}. Using default: {defaultValueSeconds} seconds");
            return defaultValueSeconds;
        }
    }

    [ExportMethod]
    public void DropUnifiedTable()
    {
        
           
            var myStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            
            
                myStore.RemoveTable(UNIFIED_TABLE_NAME);
                myStore.RemoveTable(TRACKING_TABLE_NAME);
                
               
            
        
    }
}
