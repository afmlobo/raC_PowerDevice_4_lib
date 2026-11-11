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
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Threading.Tasks;
using System.Net.Http;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using DeviceLogin;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class Log_MinMax : BaseNetLogic
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
            Log.Error("Log_MinMax", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_MinMax", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_MinMax", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_MinMax", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_MinMax", "Label 'log' not found");
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
            Log.Error("Log_MinMax", "IP address not set, cannot start download");
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
        string remoteFilePath = "/LoggingResults/Min_Max_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, "res", "TabMinMaxLog");
        string localFilePath = Path.Combine(localFolder, $"{deviceName}_MinMaxLog.csv");

        try
        {
            logLabel.Text = "Logging in...";

            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_MinMax-{deviceName}", "Cannot download Min/Max Log due to login failure.");
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
                    ProcessMinMaxLog();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_MinMax", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_MinMax", $"Failed to download log file: {ex.Message}");
            logLabel.Text = $"Failed: {ex.Message}";
        }
    }

    private class ParameterData
    {
        public int ParameterNumber { get; set; }
        public string ParameterName { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public string MinTimestamp { get; set; }
        public string MaxTimestamp { get; set; }
    }

    private IUAVariable GetVariableValue(string variableName)
    {
        var variable = LogicObject.GetVariable(variableName);
        if (variable == null)
        {
            Log.Error("Log_MinMax", $"{variableName} not found");
            return null;
        }
        return variable;
    }

    public void ProcessMinMaxLog()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, "res", "TabMinMaxLog", $"{deviceName}_MinMaxLog.csv");

            if (!File.Exists(csvFilePath))
            {
                Log.Error("Log_MinMax", $"Min/Max Log file not found at: {csvFilePath}");
                return;
            }

            var lines = File.ReadAllLines(csvFilePath);
            var headers = lines[0].Split(',');
            var data = lines.Skip(1).Select(line => line.Split(',')).ToArray();

            var parameterDataList = data.Select(row => new ParameterData
            {
                ParameterNumber = int.Parse(row[0]),
                ParameterName = GetParameterName(int.Parse(row[0])),
                MinValue = double.Parse(row[2]),
                MaxValue = double.Parse(row[3]),
                MinTimestamp = FormatTimestamp(row[4], row[5], row[6], row[7]),
                MaxTimestamp = FormatTimestamp(row[8], row[9], row[10], row[11])
            }).ToList();

            SaveJsonData(baseDirectory, parameterDataList);
            SetupDeviceSpecificHtml();
        }
        catch (Exception ex)
        {
            Log.Error("Log_MinMax", $"Error in ProcessMinMaxLog: {ex.Message}");
            if (ex.InnerException != null)
            {
                Log.Error("Log_MinMax", $"Inner Exception: {ex.InnerException.Message}");
            }
        }
    }

    private void SaveJsonData(string baseDirectory, List<ParameterData> parameterDataList)
    {
        var chartData = new
        {
            parameters = parameterDataList.Select(p => new
            {
                number = p.ParameterNumber,
                name = p.ParameterName,
                minValue = p.MinValue,
                maxValue = p.MaxValue,
                minTimestamp = p.MinTimestamp,
                maxTimestamp = p.MaxTimestamp
            }).ToArray()
        };

        string jsonFilePath = Path.Combine(baseDirectory, "res", "TabMinMaxLog", $"{deviceName}_MinMaxLogData.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonFilePath));
            File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(chartData, Formatting.Indented));
            //Log.Info("Log_MinMax", $"JSON file saved to: {jsonFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error("Log_MinMax", $"Failed to save JSON file: {ex.Message}");
        }
    }

    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res", "TabMinMaxLog");

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
                //Log.Info("Log_MinMax", $"Created directory: {resDirectory}");
            }

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_MinMaxLog.html");
            string deviceHtmlPath = Path.Combine(resDirectory, $"{deviceName}_MinMaxLog.html");

            if (!File.Exists(htmlTemplatePath))
            {
                Log.Error("Log_MinMax", $"HTML template not found at: {htmlTemplatePath}");
                return;
            }

            string htmlContent = File.ReadAllText(htmlTemplatePath);
            htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
            File.WriteAllText(deviceHtmlPath, htmlContent);
            //Log.Info("Log_MinMax", $"Generated HTML file for device {deviceName} at: {deviceHtmlPath}");

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser == null)
            {
                Log.Error("Log_MinMax", "WebBrowser control 'WebBrowser' not found");
                return;
            }

            var deviceHtmlUri = ResourceUri.FromProjectRelativePath($"res/TabMinMaxLog/{deviceName}_MinMaxLog.html");
            webBrowser.URL = deviceHtmlUri;
            
            //Log.Info("Log_MinMax", $"WebBrowser URL set to: {deviceHtmlUri.Uri}");
        }
        catch (Exception ex)
        {
            Log.Error("Log_MinMax", $"Error setting up HTML for {deviceName}: {ex.Message}");
        }
    }

    private Dictionary<int, string> GetParameterMappings()
    {
        return new Dictionary<int, string>
        {
            { 1, "V1_N_Volts" }, { 2, "V2_N_Volts" }, { 3, "V3_N_Volts" }, { 4, "VN_G_Volts" },
            { 5, "Avg_V_N_Volts" }, { 6, "V12_Volts" }, { 7, "V23_Volts" }, { 8, "V31_Volts" },
            { 9, "Avg_V_Volts" }, { 10, "I1_Amps" }, { 11, "I2_Amps" }, { 12, "I3_Amps" },
            { 13, "IN_Amps" }, { 14, "Avg_I_Amps" }, { 15, "Total_kW" }, { 16, "Total_kVAR" },
            { 17, "Total_kVA" }, { 18, "L1_kW" }, { 19, "L2_kW" }, { 20, "L3_kW" },
            { 21, "L1_kVAR" }, { 22, "L2_kVAR" }, { 23, "L3_kVAR" }, { 24, "L1_kVA" },
            { 25, "L2_kVA" }, { 26, "L3_kVA" }
        };
    }

    private string GetParameterName(int parameterNumber)
    {
        var mappings = GetParameterMappings();
        return mappings.ContainsKey(parameterNumber)
            ? mappings[parameterNumber]
            : $"Parameter {parameterNumber}";
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
        catch (Exception ex)
        {
            Log.Error("Log_MinMax", $"Error formatting timestamp: {ex.Message}");
            return "Invalid Timestamp";
        }
    }
}
