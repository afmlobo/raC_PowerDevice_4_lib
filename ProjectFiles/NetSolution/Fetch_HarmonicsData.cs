#region Using directives
using System;
using UAManagedCore;
using System.IO;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.SQLiteStore;
using FTOptix.WebUI;
using FTOptix.Store;
using FTOptix.RAEtherNetIP;
using FTOptix.MicroController;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using FTOptix.SerialPort;
using FTOptix.OPCUAServer;
using System.Text.RegularExpressions;
using System.Linq;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class Fetch_HarmonicsData : BaseNetLogic
{
    private PeriodicTask harmonicsFetchTask;
    private string deviceName;
    private string ipAddress;
    private string currentTableId = "500"; // Default table ID
    private string currentMetric = "No Selection"; // Default metric name
    private int startIndex = 0; // Start of the harmonic range
    private int endIndex = 0; // End of the harmonic range
    private LongRunningTask initializeTask;
    private const int maxHttpRetries = 3; // Number of retries for HTTP requests
    private const int retryDelayMs = 2000; // Delay between retries (2 seconds)
    private Dictionary<string, string> lastSuccessfulHarmonicsData; // Store last successful data
    private string lastSuccessfulMetric; // Store last successful metric
    private string lastRefreshTimestamp; // Store last refresh timestamp
    private bool isFirstFetch = true; // Flag for initial delay
    private IUAVariable periodVariable; // Variable for period (in ms)

    private IUANode HarmonicslistNode;

    private ComboBox Main, Sub;


    public override void Start()
    {
        try
        {
            base.Start();
            // Initialize last successful data and timestamp

            lastSuccessfulHarmonicsData = new Dictionary<string, string>();

            lastSuccessfulMetric = "No Selection";

            lastRefreshTimestamp = DateTime.Now.ToString("o"); // ISO 8601 format

            var Tag = Owner.GetAlias("Tag");
            if (Tag == null)
            {
                Log.Error("Harmonics_DataFetcher", "Tag alias not found");
                return;
            }

            var ipVariable = Tag.GetVariable("Val_IPAddress");
            if (ipVariable == null)
            {
                Log.Error("Harmonics_DataFetcher", "Val_IPAddress variable not found in Tag");
                return;
            }
            ipAddress = ipVariable.Value;
            if (string.IsNullOrEmpty(ipAddress))
            {
                Log.Error("Harmonics_DataFetcher", "IP address is null or empty");
                return;
            }

            deviceName = Tag.BrowseName;
            if (string.IsNullOrEmpty(deviceName))
            {
                Log.Error("Harmonics_DataFetcher", "Device name is null or empty");
                return;
            }

            periodVariable = Tag.GetVariable("Cfg_LiveDataUpdateRate");
            if (periodVariable == null)
            {
                Log.Error("Harmonics_DataFetcher", "Cfg_LiveDataUpdateRate variable not found");
                return;
            }

            // Start your periodics and initialization tasks as before
            initializeTask = new LongRunningTask(InitializeAsync, LogicObject);
            initializeTask.Start();

            harmonicsFetchTask = new PeriodicTask(FetchHarmonicsRefreshDataWrapper, periodVariable.Value, LogicObject);
            harmonicsFetchTask.Start();
        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Start failed: {e.Message}");
        }
    }

    public async Task FetchAndCreateHarmonicsModelsAsync()
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                string deviceIp = ipAddress; // Assume ipAddress is set earlier in your class
                string url = $"http://{deviceIp}/PowerQuality/Harmonics_Results.shtm";

                HttpResponseMessage response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("Harmonics_DataFetcher", $"Failed to fetch harmonics page from {url}, Status: {response.StatusCode}");
                    return;
                }

                string html = await response.Content.ReadAsStringAsync();

                var selectMatch = Regex.Match(html, @"<select[^>]+id=[""']pqselect[""'][^>]*>(.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!selectMatch.Success)
                {
                    Log.Error("Harmonics_DataFetcher", "Cannot find pqselect options in HTML.");
                    return;
                }

                string optionsHtml = selectMatch.Groups[1].Value;

                // Parse all options
                var optionMatches = Regex.Matches(optionsHtml, @"<option\s+value=[""'](\d+)[""'][^>]*>([^<]+)</option>", RegexOptions.IgnoreCase);

                var harmonicsList = new List<(string TableId, string FullMetric)>();
                foreach (Match m in optionMatches)
                {
                    string tableId = m.Groups[1].Value.Trim();
                    string metric = m.Groups[2].Value.Trim();
                    harmonicsList.Add((tableId, metric));
                }

                // Group by main category (e.g., Total_kW, Total_kVAR, V1_N_Volts etc.)
                var mainGroups = new Dictionary<string, List<(string TableId, string SubMetric)>>();
                foreach (var entry in harmonicsList)
                {
                    string mainKey = GetMainKey(entry.FullMetric);
                    string subMetric = entry.FullMetric;
                    if (!mainGroups.ContainsKey(mainKey))
                        mainGroups[mainKey] = new List<(string, string)>();
                    mainGroups[mainKey].Add((entry.TableId, subMetric));
                }

                // Create or reuse HarmonicslistNode in your FTOptix model
                var tag = Owner.GetAlias("Tag");
                if (tag == null)
                {
                    Log.Error("Harmonics_DataFetcher", "Tag alias not found.");
                    return;
                }

                HarmonicslistNode = tag.GetObject("Harmonicslist");
                if (HarmonicslistNode == null)
                {
                    HarmonicslistNode = InformationModel.MakeObject("Harmonicslist");
                    tag.Add(HarmonicslistNode);
                }
                // Clear existing children
                var children = HarmonicslistNode.Children;
                foreach (var child in children)
                {
                    HarmonicslistNode.Remove(child);
                }

                // Create main and sub models dynamically
                foreach (var mainEntry in mainGroups)
                {
                    var mainObj = InformationModel.MakeObject(mainEntry.Key); // e.g., "Total_kW"
                    HarmonicslistNode.Add(mainObj);

                    foreach (var subEntry in mainEntry.Value)
                    {
                        // Create a variable named after the metric
                        var varNode = InformationModel.MakeVariable(subEntry.SubMetric, OpcUa.DataTypes.String);

                        // Store TableId as the variable's value
                        varNode.Value = subEntry.TableId;

                        mainObj.Add(varNode);
                    }
                }

                //Log.Info("Harmonics_DataFetcher", "Harmonics Models created based on device data.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Harmonics_DataFetcher", $"Error in FetchAndCreateHarmonicsModelsAsync: {ex.Message}");
        }
    }


    // Helper to extract main key (adjust logic to your naming conventions)
    private string GetMainKey(string fullMetric)
    {
        var parts = fullMetric.Split('_');
        if (parts.Length > 1)
            return parts[0] + "_" + parts[1];
        else
            return fullMetric;
    }


    private async void InitializeAsync()
    {
        //Log.Info("Harmonics_DataFetcher", "InitializeAsync started!");
        try
        {
            // Step 1 — Build Harmonics model dynamically
            await FetchAndCreateHarmonicsModelsAsync();

            HarmonicslistNode = Owner.GetAlias("Tag").GetObject("Harmonicslist");
            if (HarmonicslistNode == null)
            {
                Log.Error("Harmonics_DataFetcher", "Harmonicslist node not found after creation");
                return;
            }

            Main = Owner.Get<ComboBox>("HorizontalLayout1/ComboBox_Main");
            Sub = Owner.Get<ComboBox>("HorizontalLayout1/ComboBox_Sub");

            Main.Model = HarmonicslistNode.NodeId;

            // Step 2 — Select first valid main metric (skip “No Selection”)
            IUANode firstMainMetric = null;
            if (HarmonicslistNode.Children.Count > 1)
            {
                var candidate = HarmonicslistNode.Children[1];
                if (candidate.NodeClass == NodeClass.Object)
                    firstMainMetric = candidate;
            }
            else
            {
                foreach (var child in HarmonicslistNode.Children)
                {
                    if (child.NodeClass == NodeClass.Object && child.BrowseName != "No Selection")
                    {
                        firstMainMetric = child;
                        break;
                    }
                }
            }

            if (firstMainMetric != null)
            {
                Main.SelectedItem = firstMainMetric.NodeId;

                // Set Sub ComboBox model and select its first variable
                Sub.Model = firstMainMetric.NodeId;

                IUANode firstSubMetric = null;
                foreach (var subchild in firstMainMetric.Children)
                {
                    if (subchild.NodeClass == NodeClass.Variable)
                    {
                        firstSubMetric = subchild;
                        break;
                    }
                }
                if (firstSubMetric != null)
                    Sub.SelectedItem = firstSubMetric.NodeId;
            }
            else
            {
                Log.Warning("Harmonics_DataFetcher", "No valid main metric found for default selection.");
            }

            // Step 3 — Create the default JSON file
            await CreateDefaultJsonFile();

            // Step 4 — Setup device-specific HTML in browser widget
            await SetupDeviceSpecificHtml();

            // Step 5 — Fetch the initial harmonics data from device
            await FetchInitialHarmonicsData();

        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Initialization failed: {e.Message}");
        }
    }



    private async Task CreateDefaultJsonFile()
    {
        var harmonicsData = new Dictionary<string, string>();
        var data = new
        {
            metric = "No Selection",
            harmonics = harmonicsData,
            lastRefreshTimestamp = lastRefreshTimestamp
        };

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string directory = Path.Combine(baseDirectory, "res", "TabHarmonics");
        string filePath = Path.Combine(directory, $"{deviceName}_HarmonicsData.json");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            //Log.Info("Harmonics_DataFetcher", $"Created directory: {directory}");
        }

        string jsonData = JsonConvert.SerializeObject(data, Formatting.Indented);
        await File.WriteAllTextAsync(filePath, jsonData);
        //Log.Info("Harmonics_DataFetcher", $"Created default JSON file at {filePath}");
    }

    private async Task SetupDeviceSpecificHtml()
    {
        //Log.Info("Harmonics_DataFetcher", "SetupDeviceSpecificHtml started!");
        var webBrowser = Owner.Get<WebBrowser>("HarmonicsBrowser");
        if (webBrowser == null)
        {
            Log.Error("Harmonics_DataFetcher", "WebBrowser control 'HarmonicsBrowser' not found");
            return;
        }

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string resDirectory = Path.Combine(baseDirectory, "res");

        if (!Directory.Exists(resDirectory))
        {
            Directory.CreateDirectory(resDirectory);
            //Log.Info("Harmonics_DataFetcher", $"Created directory: {resDirectory}");
        }

        string htmlTemplatePath = Path.Combine(resDirectory, "TabHarmonics", "Template_Harmonics.html");
        string deviceHtmlPath = Path.Combine(resDirectory, "TabHarmonics", $"HarmonicsDashboard_{deviceName}.html");

        if (File.Exists(htmlTemplatePath))
        {
            try
            {
                string htmlContent = await File.ReadAllTextAsync(htmlTemplatePath);
                htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
                await File.WriteAllTextAsync(deviceHtmlPath, htmlContent);

                //string tempHtmlPath = Path.Combine(baseDirectory, "res", $"HarmonicsDashboard_{deviceName}.html");
                string relativePath = $"res/TabHarmonics/HarmonicsDashboard_{deviceName}.html";
                var deviceHtmlUri = ResourceUri.FromProjectRelativePath(relativePath);
                webBrowser.URL = deviceHtmlUri;

                //Log.Info("Harmonics_DataFetcher", $"Created and loaded HTML for device {deviceName} at {deviceHtmlPath}");
            }
            catch (Exception e)
            {
                Log.Error("Harmonics_DataFetcher", $"Error setting up HTML: {e.Message}");
            }
        }
        else
        {
            Log.Error("Harmonics_DataFetcher", $"HTML template not found at {htmlTemplatePath}");
        }
    }

    [ExportMethod]
    public void OnTableIdChanged(string tableId)
    {
        if (tableId == currentTableId)
            return;

        currentTableId = tableId;

        // Try find metric in model
        string metricName = "No Selection";
        if (HarmonicslistNode != null)
        {
            foreach (var mainObj in HarmonicslistNode.Children)
            {
                if (mainObj.NodeClass != NodeClass.Object)
                    continue;

                foreach (IUAVariable varNode in mainObj.Children)
                {
                    if (varNode.NodeClass == NodeClass.Variable && varNode.Value == tableId)
                    {
                        metricName = varNode.BrowseName;
                        break;
                    }
                }

                if (metricName != "No Selection")
                    break;
            }
        }

        currentMetric = metricName;

        // Set correct harmonic range based on new metricName
        if (metricName.Contains("H1"))
        {
            startIndex = 0;
            endIndex = 31;
        }
        else if (metricName.Contains("H2"))
        {
            startIndex = 32;
            endIndex = 63;
        }
        else if (metricName.Contains("H3"))
        {
            startIndex = 64;
            endIndex = 95;
        }
        else if (metricName.Contains("H4"))
        {
            startIndex = 96;
            endIndex = 127;
        }
        else
        {
            startIndex = 0;
            endIndex = 0; // No valid range
        }

        // Set range indices as before based on currentMetric...

        var fetchTask = new LongRunningTask(() =>
        {
            FetchInitialHarmonicsData().GetAwaiter().GetResult();
        }, LogicObject);
        fetchTask.Start();
    }


    private void FetchHarmonicsRefreshDataWrapper()
    {
        //Log.Info("Harmonics_DataFetcher", "Periodic task triggered");
        Task.Run(async () =>
        {
            try
            {
                // Check device status
                IUAVariable device_status = Owner.GetAlias("Tag").GetVariable("DeviceStatus/Status");
                string status = device_status.Value;

                if (status != "Ready")
                {
                    Log.Warning("Energy_DataFetcher", $"Device status is {status}, fetch aborted.");
                    return;
                }

                if (isFirstFetch)
                {
                    //Log.Info("Harmonics_DataFetcher", "Delaying first fetch by 2 seconds");
                    await Task.Delay(2000); // Initial delay to prevent IO conflicts
                    isFirstFetch = false;
                }

                //Log.Info("Harmonics_DataFetcher", "Starting background fetch");
                await FetchHarmonicsRefreshData();
                //Log.Info("Harmonics_DataFetcher", "Background fetch completed");
            }
            catch (Exception e)
            {
                Log.Error("Harmonics_DataFetcher", $"Periodic fetch failed: {e.Message}");
            }
        });
    }

    private async Task FetchInitialHarmonicsData()
    {
        var webBrowser = Owner.Get<WebBrowser>("HarmonicsBrowser");
        webBrowser.Opacity = 50;

        try
        {
            var harmonicsData = new Dictionary<string, string>();
            if (currentTableId == "500")
            {
                var data = new
                {
                    metric = currentMetric,
                    harmonics = harmonicsData,
                    lastRefreshTimestamp = lastRefreshTimestamp
                };

                await WriteJsonFile(data, "Wrote empty harmonics data for 'No Selection'");
                lastSuccessfulHarmonicsData = harmonicsData;
                lastSuccessfulMetric = currentMetric;
                return;
            }

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/PowerQuality/cgi-bin/HarmonicsResults%7C{currentTableId}")
                {
                    Content = new StringContent("")
                };

                HttpResponseMessage response = null;
                for (int attempt = 1; attempt <= maxHttpRetries; attempt++)
                {
                    try
                    {
                        response = await client.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            break;
                        }
                        else
                        {
                            Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Initial harmonics request failed with status: {response.StatusCode}");
                            if (attempt < maxHttpRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Error fetching initial harmonics data: {e.Message}");
                        if (attempt < maxHttpRetries)
                        {
                            await Task.Delay(retryDelayMs);
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Log.Error("Harmonics_DataFetcher", "Failed to fetch initial harmonics data after multiple attempts.");
                    var data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Initial fetch failed, using last successful data");
                    return;
                }

                string responseData = await response.Content.ReadAsStringAsync();

                if (responseData.Trim() != "ok")
                {
                    Log.Error("Harmonics_DataFetcher", $"Table ID {currentTableId} selection failed: Server did not return 'ok'. Response: {responseData}");
                    var data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Initial fetch failed, using last successful data");
                    return;
                }

                await FetchHarmonicsRefreshData();
            }
        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Error fetching initial harmonics data after {maxHttpRetries} attempts: {e.Message}");
            var data = new
            {
                metric = lastSuccessfulMetric,
                harmonics = lastSuccessfulHarmonicsData,
                lastRefreshTimestamp = lastRefreshTimestamp
            };
            await WriteJsonFile(data, "Initial fetch failed, using last successful data");
        }
        finally
        {
            webBrowser.Opacity = 100;
        }
    }

    private async Task FetchHarmonicsRefreshData()
    {
        if (currentTableId == "500")
        {
            //Log.Info("Harmonics_DataFetcher", "No table ID selected (500), skipping refresh");
            return;
        }

        var harmonicsData = new Dictionary<string, string>();
        using (var client = new HttpClient())
        {
            try
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/PowerQuality/cgi-bin/HarmonicsResults_Refresh")
                {
                    Content = new StringContent("")
                };

                object data = null;

                HttpResponseMessage response = null;
                for (int attempt = 1; attempt <= maxHttpRetries; attempt++)
                {
                    try
                    {
                        response = await client.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            break;
                        }
                        else
                        {
                            Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Harmonics refresh request failed with status: {response.StatusCode}");
                            if (attempt < maxHttpRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Error fetching harmonics refresh data: {e.Message}");
                        if (attempt < maxHttpRetries)
                        {
                            await Task.Delay(retryDelayMs);
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Log.Warning("Harmonics_DataFetcher", "Harmonics refresh failed after multiple attempts, keeping last successful data");
                    data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Refresh failed, using last successful data");
                    return;
                }

                string responseData = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(responseData))
                {
                    Log.Warning("Harmonics_DataFetcher", "No harmonics data received from server (refresh), keeping last successful data");
                    data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Refresh failed, using last successful data");
                    return;
                }

                //Log.Info("Harmonics_DataFetcher", $"Raw response from HarmonicsResults_Refresh:\n{responseData}");

                string[] lines = responseData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string prefix = currentMetric;
                if (currentMetric.Contains("H1"))
                {
                    prefix = currentMetric.Replace("_H1_RMS_DC_to_31", "");
                }
                else if (currentMetric.Contains("H2"))
                {
                    prefix = currentMetric.Replace("_H2_RMS_32_to_63", "");
                }
                else if (currentMetric.Contains("H3"))
                {
                    prefix = currentMetric.Replace("_H3_RMS_64_to_95", "");
                }
                else if (currentMetric.Contains("H4"))
                {
                    prefix = currentMetric.Replace("_H4_RMS_96_to_127", "");
                }

                //Log.Info("Harmonics_DataFetcher", $"Metric={currentMetric}, Range={startIndex}-{endIndex}, Total lines={lines.Length}");

                for (int i = 0; i < lines.Length - 1; i += 2)
                {
                    string key = lines[i].Trim();
                    string value = lines[i + 1].Trim();

                    // Skip timestamps and non-harmonic metadata
                    if (key.StartsWith("Metering_"))
                        continue;

                    // Process only harmonic RMS keys
                    if (key.Contains("_H_RMS") || key.Contains("_H_Ang"))
                    {
                        string adjustedKey = key;

                        // Handle DC harmonic specially
                        if (key.Contains("_DC_"))
                        {
                            adjustedKey = adjustedKey.Replace("_H_RMS", "")
                                                     .Replace("_H_Ang", "");
                        }
                        else
                        {
                            // Only remove _H_RMS or _H_Ang, preserve the ordinal suffix
                            adjustedKey = adjustedKey.Replace("_H_RMS", "")
                                                     .Replace("_H_Ang", "");
                        }

                        // Determine harmonic order number
                        int harmonicOrder;
                        if (adjustedKey.EndsWith("_DC"))
                        {
                            harmonicOrder = 0;
                        }
                        else
                        {
                            string orderStr = adjustedKey.Substring(adjustedKey.LastIndexOf('_') + 1)
                                                         .Replace("th", "")
                                                         .Replace("st", "")
                                                         .Replace("rd", "")
                                                         .Replace("nd", "");
                            harmonicOrder = int.TryParse(orderStr, out int order) ? order : -1;
                        }

                        // Filter within range
                        if (harmonicOrder >= startIndex && harmonicOrder <= endIndex)
                        {
                            harmonicsData[adjustedKey] = value;
                        }
                    }

                }


                bool hasHarmonicsData = false;
                foreach (var key in harmonicsData.Keys)
                {
                    if (key.Contains("th") || key.EndsWith("_DC"))
                    {
                        hasHarmonicsData = true;
                        break;
                    }
                }

                if (!hasHarmonicsData)
                {
                    Log.Warning("Harmonics_DataFetcher", $"Refresh response does not contain harmonics data for the expected range ({startIndex} to {endIndex}), keeping last successful data");
                    data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Refresh failed, using last successful data");
                    return;
                }

                lastSuccessfulHarmonicsData = new Dictionary<string, string>(harmonicsData);
                lastSuccessfulMetric = currentMetric;
                lastRefreshTimestamp = DateTime.Now.ToString("o");

                data = new
                {
                    metric = currentMetric,
                    harmonics = harmonicsData,
                    lastRefreshTimestamp = lastRefreshTimestamp
                };

                await WriteJsonFile(data, "Refreshed harmonics data exported");
            }
            catch (Exception e)
            {
                Log.Warning("Harmonics_DataFetcher", $"Error fetching harmonics refresh data after {maxHttpRetries} attempts: {e.Message}, keeping last successful data");
                var data = new
                {
                    metric = lastSuccessfulMetric,
                    harmonics = lastSuccessfulHarmonicsData,
                    lastRefreshTimestamp = lastRefreshTimestamp
                };
                await WriteJsonFile(data, "Refresh failed, using last successful data");
            }
        }
    }




    private async Task WriteJsonFile(object data, string successMessage)
    {
        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string directory = Path.Combine(baseDirectory, "res", "TabHarmonics");
        string filePath = Path.Combine(directory, $"{deviceName}_HarmonicsData.json");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            string jsonString = JsonConvert.SerializeObject(data, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, jsonString);
            //Log.Info("Harmonics_DataFetcher", $"{successMessage} to {filePath}");
        }
        catch (IOException e)
        {
            Log.Error("Harmonics_DataFetcher", $"IO error writing JSON file: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Error("Harmonics_DataFetcher", $"Permission denied writing JSON file: {e.Message}");
        }
    }

    public override void Stop()
    {
        try
        {
            harmonicsFetchTask?.Dispose();
            initializeTask?.Dispose();
            Log.Info("Harmonics_DataFetcher", "Stopped fetching harmonics data");
            base.Stop();
        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Stop failed: {e.Message}");
        }
    }
}
