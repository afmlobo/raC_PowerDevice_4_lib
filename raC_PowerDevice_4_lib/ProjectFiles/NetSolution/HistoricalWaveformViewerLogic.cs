#region Using directives
using System;
using System.Linq;
using System.IO;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.WebUI;
using FTOptix.Store;
using FTOptix.Core;
using Newtonsoft.Json;
using System.Collections.Generic;
using FTOptix.CODESYS;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;
#endregion

public class HistoricalWaveformViewerLogic : BaseNetLogic
{
    private Store waveformStore;
    private const string TABLE_NAME = "WaveformArchive";
    private string deviceName;
    
    // UI References
    private DataGrid resultsDataGrid;
    private Label statusLabel;
    private Label resultsCountLabel;
    private Label WaveformLoadingMsg;
    private WebBrowser waveformWebBrowser;
    private ComboBox EventTypeFilter;
    
    // Filter Variables
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable eventTypeFilterVar;

    private IUAVariable selectedduration;
    
    private IUANode resultsModelNode;
    private LongRunningTask loadWaveformTask;

    public override void Start()
    {
        var tag = Owner.GetAlias("Tag");
        if (tag == null)
        {
            Log.Error("HistoricalWaveformViewer", "Tag alias not found");
            return;
        }
        
        deviceName = tag.BrowseName;
        
        // Get Store reference
        waveformStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
            .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_WaveformArchive");
        
        if (waveformStore == null)
        {
            Log.Error("HistoricalWaveformViewer", "Archive database not found");
            return;
        }
        
        // Get UI References
        resultsDataGrid = Owner.Get<DataGrid>("SplitContainer/VerticalLayout1/LeftPanel/ResultsDataGrid");
        statusLabel = Owner.Get<Label>("StatusPanel/HorizontalLayout1/StatusLabel");
        resultsCountLabel = Owner.Get<Label>("StatusPanel/HorizontalLayout1/ResultsCountLabel");
        waveformWebBrowser = Owner.Get<WebBrowser>("SplitContainer/VerticalLayout1/RightPanel/VerticalLayout1/WaveformWebBrowser");
        EventTypeFilter = Owner.Get<ComboBox>("FilterPanel/VerticalLayout1/HorizontalLayout1/EventTypeFilter");
        WaveformLoadingMsg = Owner.Get<Label>("SplitContainer/VerticalLayout1/RightPanel/VerticalLayout1/LoadingMsg");
        
        // Filter Variables
        startDateVar = Owner.GetVariable("StartDate");
        endDateVar = Owner.GetVariable("EndDate");
        eventTypeFilterVar = Owner.GetVariable("EventTypeFilter");
        selectedduration = Owner.GetVariable("SelectedDays");
        
        // Initialize filters
        if (startDateVar != null && endDateVar != null)
        {
            endDateVar.Value = DateTime.Now;
            startDateVar.Value = DateTime.Now.AddDays(-30);
        }

        InitializeEventTypeFilter();
        
        // Create model node for DataGrid
        resultsModelNode = InformationModel.MakeObject("WaveformResultsModel");
        tag.Add(resultsModelNode);
        
        if (resultsDataGrid != null)
            resultsDataGrid.Model = resultsModelNode.NodeId;
        
        // Hide browser initially
        if (waveformWebBrowser != null)
            waveformWebBrowser.Visible = false;
            WaveformLoadingMsg.Visible = true;
        
        // Load initial data
        LoadRecentWaveforms(30);
        
        Log.Info("HistoricalWaveformViewer", $"Viewer initialized for device: {deviceName}");
    }

    public override void Stop()
    {
        try
        {
            if (loadWaveformTask != null)
            {
                loadWaveformTask.Cancel();
                loadWaveformTask.Dispose();
                loadWaveformTask = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("HistoricalWaveformViewer", $"Failed to cancel task: {ex.Message}");
        }
    }

    [ExportMethod]
    public void SearchWaveforms()
    {
        try
        {
            DateTime startDate = startDateVar?.Value ?? DateTime.Now.AddDays(-30);
            DateTime endDate = endDateVar?.Value ?? DateTime.Now;
            eventTypeFilterVar = Owner.GetVariable("EventTypeFilter");
            string eventFilter = eventTypeFilterVar?.Value ?? "All";
            
            if (statusLabel != null)
                statusLabel.Text = "Searching...";
            
            string eventCondition = (string.IsNullOrEmpty(eventFilter) || eventFilter == "All")
                ? ""
                : $"AND EventType LIKE '%{SanitizeSqlString(eventFilter)}%'";

            var query = $@"
                SELECT 
                    Id, FileName, FileSize, TotalCycles, 
                    CaptureDateTime, EventType, FilePath,
                    MaxVoltage, MaxCurrent, CreatedAt
                FROM {TABLE_NAME}
                WHERE DeviceName = '{SanitizeSqlString(deviceName)}'
                  AND CaptureDateTime BETWEEN '{startDate:yyyy-MM-dd HH:mm:ss}' 
                      AND '{endDate:yyyy-MM-dd HH:mm:ss}'
                {eventCondition}
                ORDER BY CaptureDateTime DESC
                LIMIT 1000";

            waveformStore.Query(query, out string[] headers, out object[,] resultSet);
            
            PopulateDataGrid(resultSet);
            
            int count = resultSet?.GetLength(0) ?? 0;
            if (resultsCountLabel != null)
                resultsCountLabel.Text = $"Found {count} waveforms";
            
            if (statusLabel != null)
                statusLabel.Text = count > 0 ? "Select a waveform to view" : "No waveforms found";
            
            Log.Info("HistoricalWaveformViewer", $"Search completed: {count} results");
        }
        catch (Exception ex)
        {
            Log.Error("HistoricalWaveformViewer", $"Search failed: {ex.Message}");
            if (statusLabel != null)
                statusLabel.Text = $"Search failed: {ex.Message}";
        }
    }

    [ExportMethod]
    public void LoadRecentWaveforms(int days)
    {
        DateTime endDate = DateTime.Now;
        DateTime startDate = endDate.AddDays(-days);
        
        if (startDateVar != null) startDateVar.Value = startDate;
        if (endDateVar != null) endDateVar.Value = endDate;
        if (eventTypeFilterVar != null) eventTypeFilterVar.Value = "All";

        selectedduration.Value = days;
        SearchWaveforms();
        
    }

    [ExportMethod]
    public void OnRowClicked()
    {
        try
        {
            var selectedRow = InformationModel.Get(resultsDataGrid.UISelectedItem);
            if (selectedRow == null)
            {
                Log.Warning("HistoricalWaveformViewer", "No row selected");
                return;
            }
            
            string fileName = selectedRow.GetVariable("FileName")?.Value ?? "";
            string filePath = selectedRow.GetVariable("FilePath")?.Value ?? "";
            int fileSize = selectedRow.GetVariable("Filesize")?.Value ?? 0;
            int cycles = selectedRow.GetVariable("Cycles")?.Value ?? 0;
            string captureDateTime = selectedRow.GetVariable("DateTime")?.Value ?? "";
            string eventType = selectedRow.GetVariable("EventType")?.Value ?? "N/A";
            double maxVoltage = selectedRow.GetVariable("MaxVoltage")?.Value ?? 0.0;
            double maxCurrent = selectedRow.GetVariable("MaxCurrent")?.Value ?? 0.0;
            string archivedDate = selectedRow.GetVariable("ArchivedDate")?.Value ?? "";
            
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(filePath))
            {
                Log.Error("HistoricalWaveformViewer", "Invalid row data");
                return;
            }
            
            // Load and display waveform
            if (statusLabel != null)
                statusLabel.Text = $"Loading {fileName}...";
            
            if (waveformWebBrowser != null)
                waveformWebBrowser.Visible = false;
                WaveformLoadingMsg.Visible = true;
            
            try
            {
                if (loadWaveformTask != null)
                {
                    loadWaveformTask.Cancel();
                    loadWaveformTask.Dispose();
                }
            }
            catch { }
            
            // Pass additional metadata to the loader
            var additionalMetadata = new Dictionary<string, object>
            {
                { "databaseFileName", fileName },
                { "databaseFileSize", fileSize },
                { "databaseCycles", cycles },
                { "databaseCaptureDateTime", captureDateTime },
                { "databaseEventType", eventType },
                { "databaseMaxVoltage", maxVoltage },
                { "databaseMaxCurrent", maxCurrent },
                { "databaseArchivedDate", archivedDate }
            };
            
            loadWaveformTask = new LongRunningTask(
                task => LoadAndDisplayWaveform(filePath, fileName, additionalMetadata, task), 
                LogicObject
            );
            loadWaveformTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("HistoricalWaveformViewer", $"Failed to handle row click: {ex.Message}");
            if (statusLabel != null)
                statusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void LoadAndDisplayWaveform(string relativePath, string fileName, Dictionary<string, object> additionalMetadata, LongRunningTask task)
    {
        try
        {
            var appDirUri = new ResourceUri("%APPLICATIONDIR%\\");
            string fullPath = Path.Combine(appDirUri.Uri, relativePath);
            
            if (!File.Exists(fullPath))
            {
                throw new Exception($"Waveform file not found: {fullPath}");
            }
            
            if (task.IsCancellationRequested) return;
            
            // Parse waveform
            var parser = new OptixWaveformParser.PM5000WaveformParser();
            parser.LoadFromFile(fullPath);
            var segment = parser.GetSegment(1);
            
            if (task.IsCancellationRequested) return;
            
            // Convert to chart format with enhanced metadata
            var chartData = ConvertSegmentToChartFormat(segment, fileName, additionalMetadata);
            
            // Save JSON
            string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
            string jsonFilePath = Path.Combine(projectDir, "res", "TabWaveform", $"{deviceName}_WaveformViewerData.json");
            File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(chartData, Formatting.Indented));
            
            if (task.IsCancellationRequested) return;
            
            // Load HTML template
            string templatePath = Path.Combine(projectDir, "res", "TabWaveform", "Template_WaveformViewer.html");
            string outputHtmlPath = Path.Combine(projectDir, "res", "TabWaveform", $"{deviceName}_WaveformViewer.html");
            
            if (File.Exists(templatePath))
            {
                string templateContent = File.ReadAllText(templatePath);
                string modifiedContent = templateContent.Replace("{{deviceName}}", deviceName);
                File.WriteAllText(outputHtmlPath, modifiedContent);
            }
            
            if (task.IsCancellationRequested) return;
            
            // Display in WebBrowser
            if (waveformWebBrowser != null)
            {
                var htmlUri = ResourceUri.FromProjectRelativePath($"res/TabWaveform/{deviceName}_WaveformViewer.html");
                waveformWebBrowser.Visible = true;
                WaveformLoadingMsg.Visible = false;
                waveformWebBrowser.URL = htmlUri;
                System.Threading.Thread.Sleep(100);
                waveformWebBrowser.Refresh();
                
                if (statusLabel != null)
                    statusLabel.Text = "Waveform loaded successfully";
            }
        }
        catch (Exception ex)
        {
            Log.Error("HistoricalWaveformViewer", $"Failed to load waveform: {ex.Message}");
            if (statusLabel != null)
                statusLabel.Text = $"Failed to load waveform: {ex.Message}";
        }
    }

    private object ConvertSegmentToChartFormat(OptixWaveformParser.WaveformSegment segment, string fileName, Dictionary<string, object> additionalMetadata)
    {
        string[] headers = new string[] 
        { 
            "Time (ms)", 
            "Va (V)", "Vb (V)", "Vc (V)", "Vn (V)",
            "Ia (A)", "Ib (A)", "Ic (A)", "In (A)"
        };
        
        int totalSamples = segment.SegmentCycles * 256;
        var rows = new List<string[]>();
        double startTime = segment.DeltaTMilliseconds[0];
        
        for (int i = 0; i < totalSamples; i++)
        {
            var row = new string[9];
            row[0] = (segment.DeltaTMilliseconds[i] - startTime).ToString("F3");
            
            for (int ch = 0; ch < 8; ch++)
            {
                row[ch + 1] = segment.WaveformValues[ch, i].ToString("F6");
            }
            
            rows.Add(row);
        }
        
        // Enhanced metadata with database info
        DateTime dbCaptureDateTime;
        DateTime.TryParse(additionalMetadata["databaseCaptureDateTime"]?.ToString(), out dbCaptureDateTime);
        
        DateTime dbArchivedDate;
        DateTime.TryParse(additionalMetadata["databaseArchivedDate"]?.ToString(), out dbArchivedDate);
        
        return new
        {
            deviceName = deviceName,
            headers = headers,
            rows = rows,
            fileName = fileName,
            metadata = new
            {
                // Waveform file metadata
                captureTime = segment.CaptureTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff"),
                captureTimestamp = $"{segment.CaptureTime:HH:mm:ss}.{segment.MicrosecondOfCapture:D6}",
                totalCycles = segment.TotalWaveformCycles,
                segmentCycles = segment.SegmentCycles,
                maxVoltage = segment.MaxVoltage.ToString("F3"),
                maxCurrent = segment.MaxCurrent.ToString("F3"),
                frequency = segment.FrequencyOfCycle[0].ToString("F2") + " Hz",
                
                // Database metadata
                databaseFileName = additionalMetadata["databaseFileName"]?.ToString() ?? fileName,
                databaseFileSize = Convert.ToInt32(additionalMetadata["databaseFileSize"] ?? 0),
                databaseCycles = Convert.ToInt32(additionalMetadata["databaseCycles"] ?? 0),
                databaseCaptureDate = dbCaptureDateTime.ToString("yyyy-MM-dd"),
                databaseCaptureTime = dbCaptureDateTime.ToString("HH:mm:ss"),
                databaseEventType = additionalMetadata["databaseEventType"]?.ToString() ?? "N/A",
                databaseMaxVoltage = Convert.ToDouble(additionalMetadata["databaseMaxVoltage"] ?? 0.0),
                databaseMaxCurrent = Convert.ToDouble(additionalMetadata["databaseMaxCurrent"] ?? 0.0),
                databaseArchivedDate = dbArchivedDate != DateTime.MinValue ? dbArchivedDate.ToString("yyyy-MM-dd") : "N/A"
            }
        };
    }

    private void PopulateDataGrid(object[,] resultSet)
    {
        try
        {
            // Clear existing model
            foreach (var child in resultsModelNode.Children.ToList())
            {
                child.Delete();
            }
            
            if (resultSet == null || resultSet.GetLength(0) == 0)
            {
                Log.Info("HistoricalWaveformViewer", "No results to display");
                return;
            }
            
            for (int i = 0; i < resultSet.GetLength(0); i++)
            {
                var rowObject = InformationModel.MakeObject($"Row{i}");
                
                // Id (hidden)
                rowObject.Add(InformationModel.MakeVariable("Id", OpcUa.DataTypes.Int32));
                rowObject.GetVariable("Id").Value = Convert.ToInt32(resultSet[i, 0] ?? 0);
                
                // FileName
                rowObject.Add(InformationModel.MakeVariable("FileName", OpcUa.DataTypes.String));
                rowObject.GetVariable("FileName").Value = resultSet[i, 1]?.ToString() ?? "";
                
                // Filesize (KB)
                rowObject.Add(InformationModel.MakeVariable("Filesize", OpcUa.DataTypes.Int32));
                int fileSizeBytes = Convert.ToInt32(resultSet[i, 2] ?? 0);
                rowObject.GetVariable("Filesize").Value = fileSizeBytes / 1024;
                
                // Cycles
                rowObject.Add(InformationModel.MakeVariable("Cycles", OpcUa.DataTypes.Int32));
                rowObject.GetVariable("Cycles").Value = Convert.ToInt32(resultSet[i, 3] ?? 0);
                
                // DateTime
                rowObject.Add(InformationModel.MakeVariable("DateTime", OpcUa.DataTypes.String));
                if (resultSet[i, 4] != null && resultSet[i, 4] != DBNull.Value)
                {
                    DateTime dt = DateTime.Parse(resultSet[i, 4].ToString());
                    rowObject.GetVariable("DateTime").Value = dt.ToString("yyyy-MM-dd HH:mm");
                }
                
                // EventType
                rowObject.Add(InformationModel.MakeVariable("EventType", OpcUa.DataTypes.String));
                rowObject.GetVariable("EventType").Value = resultSet[i, 5]?.ToString() ?? "N/A";
                
                // FilePath (hidden, for loading)
                rowObject.Add(InformationModel.MakeVariable("FilePath", OpcUa.DataTypes.String));
                rowObject.GetVariable("FilePath").Value = resultSet[i, 6]?.ToString() ?? "";
                
                // MaxVoltage (hidden)
                rowObject.Add(InformationModel.MakeVariable("MaxVoltage", OpcUa.DataTypes.Double));
                rowObject.GetVariable("MaxVoltage").Value = Convert.ToDouble(resultSet[i, 7] ?? 0.0);
                
                // MaxCurrent (hidden)
                rowObject.Add(InformationModel.MakeVariable("MaxCurrent", OpcUa.DataTypes.Double));
                rowObject.GetVariable("MaxCurrent").Value = Convert.ToDouble(resultSet[i, 8] ?? 0.0);
                
                // ArchivedDate (hidden)
                rowObject.Add(InformationModel.MakeVariable("ArchivedDate", OpcUa.DataTypes.String));
                if (resultSet[i, 9] != null && resultSet[i, 9] != DBNull.Value)
                {
                    DateTime archDt = DateTime.Parse(resultSet[i, 9].ToString());
                    rowObject.GetVariable("ArchivedDate").Value = archDt.ToString("yyyy-MM-dd");
                }
                
                resultsModelNode.Add(rowObject);
            }
            
            Log.Info("HistoricalWaveformViewer", $"DataGrid populated with {resultSet.GetLength(0)} rows");
        }
        catch (Exception ex)
        {
            Log.Error("HistoricalWaveformViewer", $"Failed to populate DataGrid: {ex.Message}");
        }
    }

    private void InitializeEventTypeFilter()
    {
        try
        {
            if (EventTypeFilter == null)
            {
                Log.Warning("HistoricalWaveformViewer", "EventTypeFilter ComboBox not found");
                return;
            }
            
            // Create model object
            var eventTypeModel = InformationModel.MakeObject("EventTypeFilterModel");
            
            // Add static event type options
            var allOption = InformationModel.MakeVariable("All", OpcUa.DataTypes.String);
            allOption.Value = "All";
            eventTypeModel.Add(allOption);
            
            var voltageTransient = InformationModel.MakeVariable("VoltageTransient", OpcUa.DataTypes.String);
            voltageTransient.Value = "Voltage Transient";
            eventTypeModel.Add(voltageTransient);
            
            var currentSurge = InformationModel.MakeVariable("CurrentSurge", OpcUa.DataTypes.String);
            currentSurge.Value = "Current Surge";
            eventTypeModel.Add(currentSurge);
            
            var sag = InformationModel.MakeVariable("Sag", OpcUa.DataTypes.String);
            sag.Value = "Sag";
            eventTypeModel.Add(sag);
            
            var swell = InformationModel.MakeVariable("Swell", OpcUa.DataTypes.String);
            swell.Value = "Swell";
            eventTypeModel.Add(swell);
            
            var freqDeviation = InformationModel.MakeVariable("FrequencyDeviation", OpcUa.DataTypes.String);
            freqDeviation.Value = "Frequency Deviation";
            eventTypeModel.Add(freqDeviation);
            
            var na = InformationModel.MakeVariable("NA", OpcUa.DataTypes.String);
            na.Value = "N/A";
            eventTypeModel.Add(na);
            
            // Add model to Owner and assign to ComboBox
            Owner.Add(eventTypeModel);
            EventTypeFilter.Model = eventTypeModel.NodeId;
            
            // Set default selection to "All"
            if (eventTypeFilterVar != null)
                eventTypeFilterVar.Value = "All";
            
            Log.Info("HistoricalWaveformViewer", "EventTypeFilter initialized with static options");
        }
        catch (Exception ex)
        {
            Log.Error("HistoricalWaveformViewer", $"Failed to initialize EventTypeFilter: {ex.Message}");
        }
    }

    private string SanitizeSqlString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        
        return input.Replace("'", "''");
    }
}
