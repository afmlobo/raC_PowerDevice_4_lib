#region Using directives
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
using System.Threading.Tasks;
using System.Net.Http;
using FTOptix.EventLogger;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using FTOptix.WebUI;
using DeviceLogin;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class Log_TOU : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private LongRunningTask downloadTask;
    private Label logLabel;

    public override void Start()
    {
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Log_TOU", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_TOU", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_TOU", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_TOU", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_TOU", "Label 'log' not found");
            return;
        }

        StartDownload();
    }

    public override void Stop()
    {
        if (downloadTask != null)
        {
            downloadTask.Dispose();
            downloadTask = null;
        }
    }

    [ExportMethod]
    public void DownloadLogFileAsync()
    {
        StartDownload();
    }

    private void StartDownload()
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_TOU", "IP address not set, cannot start download");
            if (logLabel != null) logLabel.Text = "Failed: IP not set";
            return;
        }

        if (downloadTask != null)
        {
            downloadTask.Dispose();
            downloadTask = null;
        }

        downloadTask = new LongRunningTask(task => DownloadTaskMethod(task), LogicObject);
        downloadTask.Start();
    }

    private void DownloadTaskMethod(LongRunningTask task)
    {
        string remoteFilePath = "/LoggingResults/Time_of_Use_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, "res", "TabTOULog");
        string localFilePath = Path.Combine(localFolder, $"{deviceName}_TOULog.csv");

        try
        {
            logLabel.Text = "Logging in...";

            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_TOU-{deviceName}", "Cannot download TOU Log due to login failure.");
                return;
            }

            Directory.CreateDirectory(localFolder);

            logLabel.Text = "Downloading...";

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                byte[] fileBytes = client.GetByteArrayAsync(url).Result;

                if (task.IsCancellationRequested)
                {
                    logLabel.Text = "Canceled";
                    return;
                }

                File.WriteAllBytes(localFilePath, fileBytes);

                if (task.IsCancellationRequested)
                {
                    logLabel.Text = "Canceled";
                    return;
                }

                if (File.Exists(localFilePath))
                {
                    logLabel.Text = "Processing...";
                    ProcessTOULog();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_TOU", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_TOU", $"Failed to download log file: {ex.Message}");
            logLabel.Text = $"Failed: {ex.Message}";
        }
    }

    private IUAVariable GetVariableValue(string variableName)
    {
        var variable = LogicObject.GetVariable(variableName);
        if (variable == null)
        {
            Log.Error("Log_TOU", $"{variableName} not found");
            return null;
        }
        return variable;
    }

    public void ProcessTOULog()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, "res", "TabTOULog", $"{deviceName}_TOULog.csv");

            if (!File.Exists(csvFilePath))
            {
                Log.Error("Log_TOU", $"TOU Log file not found at: {csvFilePath}");
                return;
            }

            var TOUDataList = ProcessCsvFile(csvFilePath);
            SaveJsonData(baseDirectory, TOUDataList);
            SetupDeviceSpecificHtml();
        }
        catch (Exception ex)
        {
            Log.Error("Log_TOU", $"Error in ProcessTOULog: {ex.Message}");
        }
    }

    private List<TOUData> ProcessCsvFile(string csvFilePath)
    {
        var lines = File.ReadAllLines(csvFilePath);
        return lines.Skip(1)
            .Select(line =>
            {
                var row = line.Split(',');
                DateOnly StartDate = DateOnly.ParseExact(row[1], "yyMMdd", null);
                DateOnly EndDate = DateOnly.ParseExact(row[2], "yyMMdd", null);

                return new TOUData
                {
                    Record_Number = int.Parse(row[0]),
                    Start_Date = StartDate,
                    End_Date = EndDate,
                    Off_Peak_GWh_Net = float.Parse(row[3]),
                    Off_Peak_kWh_Net = float.Parse(row[4]),
                    Off_Peak_kW_Demand = float.Parse(row[5]),
                    Mid_Peak_GWh_Net = float.Parse(row[6]),
                    Mid_Peak_kWh_Net = float.Parse(row[7]),
                    Mid_Peak_kW_Demand = float.Parse(row[8]),
                    On_Peak_GWh_Net = float.Parse(row[9]),
                    On_Peak_kWh_Net = float.Parse(row[10]),
                    On_Peak_kW_Demand = float.Parse(row[11]),
                    Off_Peak_GVARh_Net = float.Parse(row[12]),
                    Off_Peak_kVARh_Net = float.Parse(row[13]),
                    Off_Peak_kVAR_Demand = float.Parse(row[14]),
                    Mid_Peak_GVARh_Net = float.Parse(row[15]),
                    Mid_Peak_kVARh_Net = float.Parse(row[16]),
                    Mid_Peak_kVAR_Demand = float.Parse(row[17]),
                    On_Peak_GVARh_Net = float.Parse(row[18]),
                    On_Peak_kVARh_Net = float.Parse(row[19]),
                    On_Peak_kVAR_Demand = float.Parse(row[20]),
                    Off_Peak_GVAh_Net = float.Parse(row[21]),
                    Off_Peak_kVAh_Net = float.Parse(row[22]),
                    Off_Peak_kVA_Demand = float.Parse(row[23]),
                    Mid_Peak_GVAh_Net = float.Parse(row[24]),
                    Mid_Peak_kVAh_Net = float.Parse(row[25]),
                    Mid_Peak_kVA_Demand = float.Parse(row[26]),
                    On_Peak_GVAh_Net = float.Parse(row[27]),
                    On_Peak_kVAh_Net = float.Parse(row[28]),
                    On_Peak_kVA_Demand = float.Parse(row[29])
                };
            })
            .ToList();
    }

    private void SaveJsonData(string baseDirectory, List<TOUData> TOUDataList)
    {
        var jsonData = TOUDataList.OrderBy(d => d.Start_Date).Select(d => new
        {
            Record_Number = d.Record_Number,
            Start_Date = d.Start_Date.ToString("yyyy-MM-dd"),
            End_Date = d.End_Date.ToString("yyyy-MM-dd"),
            Off_Peak_GWh_Net = d.Off_Peak_GWh_Net,
            Off_Peak_kWh_Net = d.Off_Peak_kWh_Net,
            Off_Peak_kW_Demand = d.Off_Peak_kW_Demand,
            Mid_Peak_GWh_Net = d.Mid_Peak_GWh_Net,
            Mid_Peak_kWh_Net = d.Mid_Peak_kWh_Net,
            Mid_Peak_kW_Demand = d.Mid_Peak_kW_Demand,
            On_Peak_GWh_Net = d.On_Peak_GWh_Net,
            On_Peak_kWh_Net = d.On_Peak_kWh_Net,
            On_Peak_kW_Demand = d.On_Peak_kW_Demand,
            Off_Peak_GVARh_Net = d.Off_Peak_GVARh_Net,
            Off_Peak_kVARh_Net = d.Off_Peak_kVARh_Net,
            Off_Peak_kVAR_Demand = d.Off_Peak_kVAR_Demand,
            Mid_Peak_GVARh_Net = d.Mid_Peak_GVARh_Net,
            Mid_Peak_kVARh_Net = d.Mid_Peak_kVARh_Net,
            Mid_Peak_kVAR_Demand = d.Mid_Peak_kVAR_Demand,
            On_Peak_GVARh_Net = d.On_Peak_GVARh_Net,
            On_Peak_kVARh_Net = d.On_Peak_kVARh_Net,
            On_Peak_kVAR_Demand = d.On_Peak_kVAR_Demand,
            Off_Peak_GVAh_Net = d.Off_Peak_GVAh_Net,
            Off_Peak_kVAh_Net = d.Off_Peak_kVAh_Net,
            Off_Peak_kVA_Demand = d.Off_Peak_kVA_Demand,
            Mid_Peak_GVAh_Net = d.Mid_Peak_GVAh_Net,
            Mid_Peak_kVAh_Net = d.Mid_Peak_kVAh_Net,
            Mid_Peak_kVA_Demand = d.Mid_Peak_kVA_Demand,
            On_Peak_GVAh_Net = d.On_Peak_GVAh_Net,
            On_Peak_kVAh_Net = d.On_Peak_kVAh_Net,
            On_Peak_kVA_Demand = d.On_Peak_kVA_Demand
        }).ToList();

        string jsonFilePath = Path.Combine(baseDirectory, "res", "TabTOULog", $"{deviceName}_TOULogData.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonFilePath));
            File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(jsonData, Formatting.Indented));
            //Log.Info("Log_TOU", $"JSON file saved to: {jsonFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error("Log_TOU", $"Failed to save JSON file: {ex.Message}");
        }
    }

    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res", "TabTOULog");

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
                //Log.Info("Log_TOU", $"Created directory: {resDirectory}");
            }

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_TOULog.html");
            string deviceHtmlPath = Path.Combine(resDirectory, $"{deviceName}_TOULog.html");

            if (!File.Exists(htmlTemplatePath))
            {
                Log.Error("Log_TOU", $"HTML template not found at: {htmlTemplatePath}");
                return;
            }

            string htmlContent = File.ReadAllText(htmlTemplatePath);
            htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
            File.WriteAllText(deviceHtmlPath, htmlContent);
            //Log.Info("Log_TOU", $"Generated HTML file for device {deviceName} at: {deviceHtmlPath}");

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser == null)
            {
                Log.Error("Log_TOU", "WebBrowser control 'WebBrowser' not found");
                return;
            }

            var deviceHtmlUri = ResourceUri.FromProjectRelativePath($"res/TabTOULog/{deviceName}_TOULog.html");
            webBrowser.URL = deviceHtmlUri;
            //Log.Info("Log_TOU", $"WebBrowser URL set to: {deviceHtmlUri.Uri}");
        }
        catch (Exception ex)
        {
            Log.Error("Log_TOU", $"Error setting up HTML for {deviceName}: {ex.Message}");
        }
    }
}

public class TOUData
{
    public int Record_Number { get; set; }
    public DateOnly Start_Date { get; set; }
    public DateOnly End_Date { get; set; }
    public float Off_Peak_GWh_Net { get; set; }
    public float Off_Peak_kWh_Net { get; set; }
    public float Off_Peak_kW_Demand { get; set; }
    public float Mid_Peak_GWh_Net { get; set; }
    public float Mid_Peak_kWh_Net { get; set; }
    public float Mid_Peak_kW_Demand { get; set; }
    public float On_Peak_GWh_Net { get; set; }
    public float On_Peak_kWh_Net { get; set; }
    public float On_Peak_kW_Demand { get; set; }
    public float Off_Peak_GVARh_Net { get; set; }
    public float Off_Peak_kVARh_Net { get; set; }
    public float Off_Peak_kVAR_Demand { get; set; }
    public float Mid_Peak_GVARh_Net { get; set; }
    public float Mid_Peak_kVARh_Net { get; set; }
    public float Mid_Peak_kVAR_Demand { get; set; }
    public float On_Peak_GVARh_Net { get; set; }
    public float On_Peak_kVARh_Net { get; set; }
    public float On_Peak_kVAR_Demand { get; set; }
    public float Off_Peak_GVAh_Net { get; set; }
    public float Off_Peak_kVAh_Net { get; set; }
    public float Off_Peak_kVA_Demand { get; set; }
    public float Mid_Peak_GVAh_Net { get; set; }
    public float Mid_Peak_kVAh_Net { get; set; }
    public float Mid_Peak_kVA_Demand { get; set; }
    public float On_Peak_GVAh_Net { get; set; }
    public float On_Peak_kVAh_Net { get; set; }
    public float On_Peak_kVA_Demand { get; set; }
}
