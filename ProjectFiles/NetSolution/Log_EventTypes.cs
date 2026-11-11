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

public class Log_EventTypes : BaseNetLogic
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
            Log.Error("Log_EventTypes", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_EventTypes", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_EventTypes", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_EventTypes", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_EventTypes", "Label 'log' not found");
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
            Log.Error("Log_EventTypes", "IP address not set, cannot start download");
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
        string remoteFilePath = "/LoggingResults/Event_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        //string localFolder = Path.Combine(projectDir, deviceName);
        string localFilePath = Path.Combine(projectDir, "res","TabEventLog",$"Event_Log_{deviceName}.csv");

        try
        {
            logLabel.Text = "Logging in...";

            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_EventTypes-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }
            
            //Directory.CreateDirectory(localFolder);

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
                    ProcessEventLog();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_EventTypes", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_EventTypes", $"Failed to download log file: {ex.Message}");
            logLabel.Text = $"Failed: {ex.Message}";
        }
    }

    private class EventData
    {
        public int RecordIdentifier { get; set; }
        public string Timestamp { get; set; }
        public int EventType { get; set; }
        public string EventTypeName { get; set; }
        public int GeneralCode { get; set; }
        public string GeneralCodeDescription { get; set; }
        public int InformationCode { get; set; }
        public string InformationCodeDescription { get; set; }
        public string Severity { get; set; }
    }

    private readonly Dictionary<int, string> _eventTypes = new()
    {
        { 1, "Self-Test Status" },
        { 2, "Configuration Changed" },
        { 4, "Log Cleared or Set" },
        { 8, "Relay/KYZ Output Forced" },
        { 16, "Status Input Activated" },
        { 32, "Status Input Deactivated" },
        { 64, "Energy Register Rollover" },
        { 128, "Device Power Up" },
        { 256, "Device Power Down" },
        { 512, "Missed External Demand Sync" },
        { 1024, "Register Set Clear" },
        { 2048, "Waveform Log Full" },
        { 4096, "Reset Event" }
    };

    private readonly Dictionary<int, Dictionary<int, (string Description, Dictionary<int, string> InformationCodes)>> _cascadedDecoding = new()
    {
        {
            1, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 0, ("Pass", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 1, ("Nor Flash Memory", new Dictionary<int, string>
                    {
                        { 1, "Overall Status" },
                        { 2, "Boot Code Checksum" },
                        { 4, "Application Code Checksum" },
                        { 8, "Wrong Application FRN" },
                        { 16, "Invalid Model Type" },
                        { 32, "WIN Mismatch" },
                        { 64, "Missing Upgrade Block" }
                    })
                },
                { 2, ("SDRAM", new Dictionary<int, string> { { 1, "Failed Read/Write Test" } }) },
                { 4, ("NAND Flash Memory", new Dictionary<int, string> { { 1, "Read/Write Failed" } }) },
                { 8, ("FRAM", new Dictionary<int, string> { { 1, "Failed Read/Write Test" } }) },
                { 16, ("Real Time Clock", new Dictionary<int, string>
                    {
                        { 1, "Real Time Clock Failed" },
                        { 2, "Real Time Clock not Set" }
                    })
                },
                { 32, ("Watchdog Timer", new Dictionary<int, string> { { 1, "Watchdog Time Out" } }) },
                { 64, ("Ethernet Communication", new Dictionary<int, string>
                    {
                        { 1, "Ethernet Communication Port Failed" },
                        { 2, "SNTP Task Init Failed" },
                        { 4, "Demand Broadcast Task Init Failed" }
                    })
                }
            }
        },
        {
            2, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("Clock Set", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("Status Input Counter Set", new Dictionary<int, string>
                    {
                        { 1, "Status Input 1" },
                        { 2, "Status Input 2" },
                        { 4, "Status Input 3" },
                        { 8, "Status Input 4" }
                    })
                },
                { 4, ("Factory Defaults Restored", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 8, ("Energy Register Set", new Dictionary<int, string>
                    {
                        { 1, "Wh Register" },
                        { 2, "VARh Register" },
                        { 4, "VAh Register" },
                        { 8, "Ah Register" },
                        { 16, "All Energy Registers Cleared" }
                    })
                },
                { 16, ("Terminal Locked", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 32, ("Terminal Unlocked", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            4, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("Min/Max Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("Energy Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 4, ("LoadFactor Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 8, ("TOU Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 16, ("Data Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 32, ("Setpoint Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 64, ("Trigger Data Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 128, ("Power Quality Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 256, ("Waveform Log Cleared", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            8, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("KYZ Forced On", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("KYZ Forced Off", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 4, ("Relay 1 Forced On", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 8, ("Relay 1 Forced Off", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 16, ("Relay 2 Forced On", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 32, ("Relay 2 Forced Off", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 64, ("Relay 3 Forced On", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 128, ("Relay 3 Forced Off", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            16, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("Status Input 1", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("Status Input 2", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 4, ("Status Input 3", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 8, ("Status Input 4", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            32, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("Status Input 1", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("Status Input 2", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 4, ("Status Input 3", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 8, ("Status Input 4", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            64, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("Wh Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("VARh Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 4, ("VAh Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 8, ("Status Input 1 Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 16, ("Status Input 2 Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 32, ("Status Input 3 Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 64, ("Status Input 4 Register", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            128, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 0, ("Device Power Up", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            256, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 0, ("Device Power Down", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            512, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 0, ("Missed External Demand Sync", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            1024, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 0, ("Register Set Clear", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            2048, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 0, ("Waveform Log Full", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        },
        {
            4096, new Dictionary<int, (string, Dictionary<int, string>)>
            {
                { 1, ("Command Reset", new Dictionary<int, string> { { 0, "No Additional Info" } }) },
                { 2, ("System Error Reset", new Dictionary<int, string> { { 0, "No Additional Info" } }) }
            }
        }
    };

    [ExportMethod]
    public void ProcessEventLog()
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, "res","TabEventLog", $"Event_Log_{deviceName}.csv");


            if (!File.Exists(csvFilePath))
            {
                Log.Error("Event Log file not found at: " + csvFilePath);
                return;
            }

            var eventDataList = ProcessCsvFile(csvFilePath);
            SaveJsonData(baseDirectory, eventDataList);
            SetupDeviceSpecificHtml();
        }
        catch (Exception ex)
        {
            Log.Error($"Error in ProcessEventLog: {ex.Message}");
        }
    }

    private List<EventData> ProcessCsvFile(string csvFilePath)
    {
        var lines = File.ReadAllLines(csvFilePath);
        return lines.Skip(1)
            .Select(line =>
            {
                var row = line.Split(',');
                var eventType = int.Parse(row[5]);
                var generalCode = int.Parse(row[6]);
                var informationCode = int.Parse(row[7]);

                return new EventData
                {
                    RecordIdentifier = int.Parse(row[0]),
                    Timestamp = FormatTimestamp(row[1], row[2], row[3], row[4]),
                    EventType = eventType,
                    EventTypeName = GetEventTypeDescription(eventType),
                    GeneralCode = generalCode,
                    GeneralCodeDescription = GetGeneralCodeDescription(eventType, generalCode),
                    InformationCode = informationCode,
                    InformationCodeDescription = GetInformationCodeDescription(eventType, generalCode, informationCode),
                    Severity = GetSeverity(eventType)
                };
            })
            .ToList();
    }

    private string GetEventTypeDescription(int eventType)
    {
        return _eventTypes.GetValueOrDefault(eventType, $"Unknown Event Type ({eventType})");
    }

    private string GetGeneralCodeDescription(int eventType, int generalCode)
    {
        if (_cascadedDecoding.TryGetValue(eventType, out var generalCodes) &&
            generalCodes.TryGetValue(generalCode, out var generalCodeInfo))
        {
            return generalCodeInfo.Description;
        }
        return $"Unknown General Code ({generalCode}) for Event Type {eventType}";
    }

    private string GetInformationCodeDescription(int eventType, int generalCode, int informationCode)
    {
        if (_cascadedDecoding.TryGetValue(eventType, out var generalCodes) &&
            generalCodes.TryGetValue(generalCode, out var generalCodeInfo) &&
            generalCodeInfo.InformationCodes.TryGetValue(informationCode, out var description))
        {
            return description;
        }
        return $"Unknown Information Code ({informationCode}) for General Code {generalCode} and Event Type {eventType}";
    }

    private string GetSeverity(int eventType)
    {
        return eventType switch
        {
            128 or 256 => "warning",
            512 or 1024 or 2048 => "error",
            4096 => "alarm",
            _ => "info"
        };
    }

    private void SaveJsonData(string baseDirectory, List<EventData> eventDataList)
    {
        var jsonData = new
        {
            headers = new[] { "Record_ID", "Timestamp", "Event_Type", "General_Code", "Information_Code", "Severity" },
            events = eventDataList.Select(e => new
            {
                id = e.RecordIdentifier,
                timestamp = e.Timestamp,
                eventType = e.EventTypeName,
                generalCode = e.GeneralCodeDescription,
                infoCode = e.InformationCodeDescription,
                severity = e.Severity
            }).ToArray()
        };

        string jsonFilePath = Path.Combine(baseDirectory, "res","TabEventLog",$"{deviceName}_EventLogData.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonFilePath));
            File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(jsonData, Formatting.Indented));
            //Log.Info("Log_EventTypes", $"JSON file saved to: {jsonFilePath}");

            // var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            // if (webBrowser != null)
            // {
            //     string relativePath = $"res/TabEventLog/{deviceName}_EventLog.html";
            //     var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
            //     webBrowser.URL = templatePath;
            //     //Log.Info("Log_EventTypes", $"WebBrowser URL set to: {templatePath.Uri}");
            // }
            // else
            // {
            //     Log.Error("Log_EventTypes", "WebBrowser control not found in the UI");
            // }
        }
        catch (Exception ex)
        {
            Log.Error("Log_EventTypes", $"Failed to save JSON file: {ex.Message}");
        }
    }


    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res", "TabEventLog");

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
                //Log.Info("TabEventLog", $"Created directory: {resDirectory}");
            }

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_EventLog.html");
            string deviceHtmlPath = Path.Combine(resDirectory, $"{deviceName}_EventLog.html");

            if (!File.Exists(htmlTemplatePath))
            {
                Log.Error("TabEventLog", $"HTML template not found at {htmlTemplatePath}");
                return;
            }

            string htmlContent = File.ReadAllText(htmlTemplatePath);
            htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
            File.WriteAllText(deviceHtmlPath, htmlContent);
            //Log.Info("TabEventLog", $"Generated HTML file for device {deviceName} at {deviceHtmlPath}");

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser == null)
            {
                Log.Error("TabEventLog", "WebBrowser control 'WebBrowser' not found");
                return;
            }

            var deviceHtmlUri = ResourceUri.FromProjectRelativePath($"res/TabEventLog/{deviceName}_EventLog.html");
            webBrowser.URL = deviceHtmlUri;
            //Log.Info("TabEventLog", $"WebBrowser URL set to res/{deviceName}_EventLog.html");
        }
        catch (Exception e)
        {
            Log.Error("TabEventLog", $"Error setting up HTML for {deviceName}: {e.Message}");
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
        catch (Exception ex)
        {
            Log.Error($"Error formatting timestamp: {ex.Message}");
            return "Invalid Timestamp";
        }
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
}
