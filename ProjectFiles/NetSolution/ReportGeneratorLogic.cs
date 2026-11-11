#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using FTOptix.DataLogger;
using FTOptix.SerialPort;
#endregion

/// <summary>
/// ReportGeneratorLogic: Unified report generation for both manual and scheduled triggers
/// - Runs periodic task every 60 seconds to check schedule triggers
/// - Provides ExportMethod for manual instant report generation
/// - Generates energy reports with KPIs, charts, and PDF output
/// </summary>
public class ReportGeneratorLogic : BaseNetLogic
{
    private IUANode schedulesFolder;
    private Store commonStore;
    private System.Threading.Timer scheduleCheckTimer;
    private string reportsBasePath;

    private const string ENERGY_LOG_TABLE = "EnergyLog_AllDevices";
    private const int SCHEDULE_CHECK_INTERVAL_MS = 60000; // Check every 60 seconds

    public override void Start()
    {
        try
        {
            // Get schedules folder
            schedulesFolder = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ReportSchedules");
            if (schedulesFolder == null)
            {
                Log.Warning("ReportGeneratorLogic", "Schedules folder not found");
                return;
            }

            // Get database store
            commonStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (commonStore == null)
            {
                Log.Error("ReportGeneratorLogic", "Database store not found!");
                return;
            }

            // Start periodic schedule checker
            scheduleCheckTimer = new System.Threading.Timer(
                CheckSchedulesCallback,
                null,
                0,
                SCHEDULE_CHECK_INTERVAL_MS
            );

            // Initialize reports storage path
            InitializeReportsStorage();
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error initializing: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialize reports storage path from configuration or default to %APPLICATIONDIR%
    /// </summary>
    private void InitializeReportsStorage()
    {
        try
        {
            // Read storage path from variable (optional - can be configured by user)
            string configuredStoragePath = ReadStoragePath("Cfg_ReportsStoragePath", "%APPLICATIONDIR%");
            
            reportsBasePath = Path.Combine(configuredStoragePath, "Reports");
            
            // Create base directory if it doesn't exist
            if (!Directory.Exists(reportsBasePath))
            {
                Directory.CreateDirectory(reportsBasePath);
                Log.Info("ReportGeneratorLogic", $"Created reports directory: {reportsBasePath}");
            }
            
            Log.Info("ReportGeneratorLogic", $"Reports storage initialized at: {reportsBasePath}");
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Failed to initialize reports storage: {ex.Message}");
            Log.Warning("ReportGeneratorLogic", "Falling back to %APPLICATIONDIR%");
            
            // Fallback to application directory
            string fallbackUri = new ResourceUri("%APPLICATIONDIR%\\").Uri;
            reportsBasePath = Path.Combine(fallbackUri, "Reports");
            try
            {
                Directory.CreateDirectory(reportsBasePath);
            }
            catch (Exception fallbackEx)
            {
                Log.Error("ReportGeneratorLogic", $"Fallback also failed: {fallbackEx.Message}");
            }
        }
    }

    public override void Stop()
    {
        if (scheduleCheckTimer != null)
        {
            scheduleCheckTimer.Dispose();
            scheduleCheckTimer = null;
            Log.Info("ReportGeneratorLogic", "Schedule checker stopped");
        }
    }

    /// <summary>
    /// Periodic callback to check and execute scheduled reports
    /// </summary>
    private void CheckSchedulesCallback(object state)
    {
        try
        {
            if (schedulesFolder == null || commonStore == null)
                return;

            DateTime now = DateTime.Now;
            int schedulesProcessed = 0;

            // Iterate through all schedules
            foreach (var scheduleNode in schedulesFolder.Children)
            {
                try
                {
                    var isActiveVar = scheduleNode.GetVariable("IsActive");
                    var nextRunVar = scheduleNode.GetVariable("NextRun");
                    var locationPathVar = scheduleNode.GetVariable("LocationPath");
                    var frequencyVar = scheduleNode.GetVariable("Frequency");
                    var timeOfDayVar = scheduleNode.GetVariable("TimeOfDay");
                    var scheduleIdVar = scheduleNode.GetVariable("ScheduleId");

                    // Check if active
                    if (isActiveVar?.Value != true)
                        continue;

                    // Check if it's time to run
                    DateTime nextRun = nextRunVar?.Value ?? DateTime.MaxValue;
                    if (nextRun > DateTime.Now)
                        continue;

                    // Extract schedule parameters
                    string orgPath = locationPathVar?.Value ?? "";
                    string frequency = frequencyVar?.Value ?? "Daily";
                    string timeOfDay = timeOfDayVar?.Value ?? "09:00";
                    string scheduleId = scheduleIdVar?.Value ?? "Unknown";

                    // Get DayOfWeek and DayOfMonth
                    int dayOfWeek = scheduleNode.GetVariable("DayOfWeek")?.Value ?? 0;
                    int dayOfMonth = scheduleNode.GetVariable("DayOfMonth")?.Value ?? 0;

                    // Generate report
                    GenerateReport(orgPath, frequency, scheduleId, isScheduled: true);

                    // Update LastRun and NextRun
                    scheduleNode.GetVariable("LastRun").Value = DateTime.Now;
                    DateTime newNextRun = CalculateNextRunTime(frequency, timeOfDay, dayOfWeek, dayOfMonth);
                    scheduleNode.GetVariable("NextRun").Value = newNextRun;

                    schedulesProcessed++;
                }
                catch (Exception ex)
                {
                    Log.Warning("ReportGeneratorLogic", $"Error processing schedule {scheduleNode.BrowseName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error in schedule check: {ex.Message}");
        }
    }

    /// <summary>
    /// Manual report generation - called from UI button
    /// </summary>
    [ExportMethod]
    public void GenerateManualReport(string orgPath, string reportPeriod)
    {
        try
        {
            // Pass the reportPeriod directly - will be handled in GenerateReport
            GenerateReport(orgPath, reportPeriod, "Manual_" + Guid.NewGuid().ToString().Substring(0, 8), isScheduled: false);
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error generating manual report: {ex.Message}");
        }
    }

    /// <summary>
    /// Core report generation logic (shared by scheduled and manual)
    /// </summary>
    private void GenerateReport(string orgPath, string frequency, string reportId, bool isScheduled)
    {
        try
        {
            // Calculate date range based on frequency/period
            DateTime endDate = DateTime.Now;
            DateTime startDate;
            
            // Handle different report periods
            switch (frequency)
            {
                case "Last24Hours":
                case "Daily":
                    startDate = endDate.AddDays(-1);
                    break;
                    
                case "Last7Days":
                case "Weekly":
                    startDate = endDate.AddDays(-7);
                    break;
                    
                case "Last30Days":
                case "Monthly":
                    startDate = endDate.AddDays(-30);
                    break;
                    
                case "CurrentMonth":
                    // First day of current month at 00:00:00
                    startDate = new DateTime(endDate.Year, endDate.Month, 1, 0, 0, 0);
                    break;
                    
                default:
                    startDate = endDate.AddDays(-30);
                    break;
            }
            
            int daysInRange = (int)Math.Round((endDate - startDate).TotalDays);

            // Get devices from org path
            var deviceNames = GetDeviceNamesFromOrgNode(orgPath);
            if (deviceNames.Count == 0)
            {
                Log.Warning("ReportGeneratorLogic", $"No devices found for org path: {orgPath}");
                return;
            }

            // Fetch and aggregate data
            var kpis = CalculateEnergyKPIs(deviceNames, startDate, endDate);
            var energyTrend = GetEnergyTrendData(deviceNames, startDate, endDate, daysInRange);
            var deviceEnergy = GetDeviceEnergyDistribution(deviceNames, startDate, endDate);
            var demand = GetDemandProfileData(deviceNames, startDate, endDate, daysInRange);
            var activeReactive = GetActiveReactiveData(deviceNames, startDate, endDate);
            var powerFactor = GetPowerFactorData(deviceNames, startDate, endDate, daysInRange);
            var status = GetDeviceStatusDistribution(deviceNames, startDate, endDate);

            // Build dashboard data object
            var dashboardData = new
            {
                orgName = StripModelPrefix(orgPath),
                deviceCount = deviceNames.Count,
                timeRange = GetTimeRangeDescription(daysInRange),
                startDate = startDate.ToString("yyyy-MM-dd HH:mm:ss"),
                endDate = endDate.ToString("yyyy-MM-dd HH:mm:ss"),
                lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                kpis,
                energyTrendChart = energyTrend,
                deviceEnergyChart = deviceEnergy,
                demandChart = demand,
                activeReactiveChart = activeReactive,
                powerFactorChart = powerFactor,
                statusChart = status
            };

            // Save JSON
            string jsonFileName = SaveReportJSON(dashboardData, orgPath, frequency, reportId);

            // Generate PDF synchronously (moved out of background task to ensure it completes)
            try
            {
                Log.Info("ReportGeneratorLogic", $"Starting PDF generation for report {reportId}");
                
                string pdfFileName = GeneratePDFReport(
                    dashboardData,
                    StripModelPrefix(orgPath),
                    GetTimeRangeDescription(daysInRange),
                    orgPath,
                    frequency,
                    reportId
                );
                
                Log.Info("ReportGeneratorLogic", $"PDF generated successfully: {pdfFileName}");
            }
            catch (Exception ex)
            {
                Log.Error("ReportGeneratorLogic", $"PDF generation failed for {reportId}: {ex.Message}");
                if (ex.InnerException != null)
                    Log.Error("ReportGeneratorLogic", $"Inner exception: {ex.InnerException.Message}");
                Log.Error("ReportGeneratorLogic", $"Stack trace: {ex.StackTrace}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error generating report {reportId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Save report data to JSON file in persistent application directory
    /// </summary>
    private string SaveReportJSON(object dashboardData, string orgPath, string frequency, string reportId)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dashboardData, options);

            // Use configured storage path
            string directory = reportsBasePath;

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string sanitizedOrg = SanitizeForFilename(StripModelPrefix(orgPath));
            string fileName = $"Report_{sanitizedOrg}_{frequency}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string filePath = Path.Combine(directory, fileName);

            File.WriteAllText(filePath, json);

            return fileName;
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error saving JSON: {ex.Message}");
            throw;
        }
    }

   
    private string GeneratePDFReport(object dashboardData, string orgName, string period, string orgPath, string frequency, string reportId)
    {
        try
        {
            Log.Info("ReportGeneratorLogic", $"PDF generation started for {orgName} - {frequency}");
            
            QuestPDF.Settings.License = LicenseType.Community;

            // Use configured storage path
            string directory = reportsBasePath;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                Log.Info("ReportGeneratorLogic", $"Created Reports directory: {directory}");
            }

            // Generate filename: Report_{OrgPath}_{Frequency}_{Date}.pdf
            string sanitizedOrg = SanitizeForFilename(orgName);
            string fileName = $"Report_{sanitizedOrg}_{frequency}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string pdfPath = Path.Combine(directory, fileName);
            
            Log.Info("ReportGeneratorLogic", $"PDF will be saved to: {pdfPath}");

            // Extract KPI values and date range from dashboard data (anonymous object) via JSON projection
            double totalEnergy = 0, peakDemand = 0, avgPF = 0, reactiveEnergy = 0, apparentEnergy = 0;
            string startDateStr = string.Empty, endDateStr = string.Empty;

            try
            {
                var dashboardElement = JsonSerializer.SerializeToElement(dashboardData);
                if (dashboardElement.ValueKind == JsonValueKind.Object)
                {
                    if (dashboardElement.TryGetProperty("kpis", out var kpiElement))
                    {
                        double TryGet(JsonElement el, string name)
                        {
                            return el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
                                ? prop.GetDouble()
                                : 0;
                        }

                        totalEnergy = TryGet(kpiElement, "totalEnergy");
                        peakDemand = TryGet(kpiElement, "peakDemand");
                        avgPF = TryGet(kpiElement, "avgPowerFactor");
                        reactiveEnergy = TryGet(kpiElement, "reactiveEnergy");
                        apparentEnergy = TryGet(kpiElement, "apparentEnergy");
                    }

                    if (dashboardElement.TryGetProperty("startDate", out var startEl) && startEl.ValueKind == JsonValueKind.String)
                        startDateStr = startEl.GetString();
                    if (dashboardElement.TryGetProperty("endDate", out var endEl) && endEl.ValueKind == JsonValueKind.String)
                        endDateStr = endEl.GetString();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("ReportGeneratorLogic", $"Error extracting KPI data for PDF: {ex.Message}");
            }

            // Generate PDF using QuestPDF
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(40);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Arial"));

                    page.Header().Column(column =>
                    {
                        column.Item().AlignCenter().Text("Energy Report")
                            .FontSize(24).Bold().FontColor("#007bff");
                        column.Item().AlignCenter().PaddingTop(5).Text(text =>
                        {
                            text.Span("Organization: ").Bold();
                            text.Span(orgName);
                            text.Span(" | Period: ").Bold();
                            text.Span(period);
                        });
                        column.Item().AlignCenter().Text($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                            .FontSize(9).FontColor("#666");
                        column.Item().PaddingTop(10).BorderBottom(2).BorderColor("#007bff");
                    });

                    page.Content().PaddingTop(20).Column(column =>
                    {
                        // KPI Cards
                        column.Item().Text("Key Performance Indicators")
                            .FontSize(16).Bold().FontColor("#333");

                        column.Item().PaddingTop(10).Row(row =>
                        {
                            row.RelativeItem().PaddingRight(5).Background("#667eea").Padding(15).Column(col =>
                            {
                                col.Item().Text("Total Energy").FontSize(10).FontColor("#ffffff");
                                col.Item().PaddingTop(5).Text($"{totalEnergy:N2}").FontSize(24).Bold().FontColor("#ffffff");
                                col.Item().Text("kWh").FontSize(12).FontColor("#ffffff");
                            });
                            row.RelativeItem().PaddingLeft(5).Background("#f5576c").Padding(15).Column(col =>
                            {
                                col.Item().Text("Peak Demand").FontSize(10).FontColor("#ffffff");
                                col.Item().PaddingTop(5).Text($"{peakDemand:N2}").FontSize(24).Bold().FontColor("#ffffff");
                                col.Item().Text("kW").FontSize(12).FontColor("#ffffff");
                            });
                        });

                        column.Item().PaddingTop(10).Row(row =>
                        {
                            row.RelativeItem().PaddingRight(5).Background("#00f2fe").Padding(15).Column(col =>
                            {
                                col.Item().Text("Avg Power Factor").FontSize(10).FontColor("#ffffff");
                                col.Item().PaddingTop(5).Text($"{avgPF:N3}").FontSize(24).Bold().FontColor("#ffffff");
                                col.Item().Text("PF").FontSize(12).FontColor("#ffffff");
                            });
                            row.RelativeItem().PaddingLeft(5).Background("#38f9d7").Padding(15).Column(col =>
                            {
                                col.Item().Text("Reactive Energy").FontSize(10).FontColor("#ffffff");
                                col.Item().PaddingTop(5).Text($"{reactiveEnergy:N2}").FontSize(24).Bold().FontColor("#ffffff");
                                col.Item().Text("kVARh").FontSize(12).FontColor("#ffffff");
                            });
                        });

                        // Summary
                        column.Item().PaddingTop(30).Text("Report Summary")
                            .FontSize(14).Bold().FontColor("#333");

                        column.Item().PaddingTop(10).Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(2);
                                columns.RelativeColumn(3);
                            });

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Organization").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text(orgName);

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Period").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text(period);

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Start Date").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text(string.IsNullOrEmpty(startDateStr) ? "" : startDateStr);

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("End Date").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text(string.IsNullOrEmpty(endDateStr) ? "" : endDateStr);

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Total Energy (Net)").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text($"{totalEnergy:N2} kWh");

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Peak Demand").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text($"{peakDemand:N2} kW");

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Avg Power Factor").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text($"{avgPF:N3}");

                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text("Apparent Energy").Bold().FontColor("#666");
                            table.Cell().BorderBottom(1).BorderColor("#eee").Padding(8).Text($"{apparentEnergy:N2} kVAh");
                        });
                    });

                    page.Footer().AlignCenter().Column(column =>
                    {
                        column.Item().BorderTop(1).BorderColor("#eee").PaddingTop(10)
                            .Text("Generated by Rockwell FactoryTalk Optix Energy Monitoring System - raC_4_01_raC_Dvc_PM5000_PQEMDashboard_UI")
                            .FontSize(9).FontColor("#999");
                    });
                });
            }).GeneratePdf(pdfPath);

            Log.Info("ReportGeneratorLogic", $"PDF file successfully created: {pdfPath}");
            
            // Verify file exists
            if (File.Exists(pdfPath))
            {
                var fileInfo = new FileInfo(pdfPath);
                Log.Info("ReportGeneratorLogic", $"PDF file verified - Size: {fileInfo.Length} bytes");
            }
            else
            {
                Log.Error("ReportGeneratorLogic", $"PDF file not found after generation: {pdfPath}");
            }

            return fileName;
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error generating PDF: {ex.Message}");
            Log.Error("ReportGeneratorLogic", $"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
                Log.Error("ReportGeneratorLogic", $"Inner exception: {ex.InnerException.Message}");
            throw;
        }
    }

    // ==================== DATA FETCHING METHODS ====================

    private object CalculateEnergyKPIs(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        double totalKWh = 0, peakDemandKW = 0, avgPowerFactor = 0, totalKVARh = 0, totalKVAh = 0;
        int deviceCount = 0;

        try
        {
            // Use ISO format with 'T' separator to match Dashboard
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");

            foreach (var deviceName in deviceNames)
            {
                string escapedDeviceName = deviceName.Replace("'", "''");

                // Total energy - Use Total_kWh_Net to match Dashboard
                string query1 = "SELECT MAX(Total_kWh_Net) as MaxVal, MIN(Total_kWh_Net) as MinVal FROM " + ENERGY_LOG_TABLE +
                               " WHERE DeviceName = '" + escapedDeviceName + "' AND Timestamp >= '" + startDateStr + "' AND Timestamp < '" + endDateStr + "'";

                object[,] results1;
                string[] columns1;
                commonStore.Query(query1, out columns1, out results1);

                if (results1 != null && results1.GetLength(0) > 0 && results1[0, 0] != DBNull.Value && results1[0, 1] != DBNull.Value)
                {
                    double maxVal = Convert.ToDouble(results1[0, 0]);
                    double minVal = Convert.ToDouble(results1[0, 1]);
                    totalKWh += (maxVal - minVal);
                }

                // Reactive/Apparent energy - Use Total_kVARh_Net and Total_kVAh to match Dashboard
                string query2 = "SELECT MAX(Total_kVARh_Net) as MaxKVARh, MIN(Total_kVARh_Net) as MinKVARh, MAX(Total_kVAh) as MaxKVAh, MIN(Total_kVAh) as MinKVAh FROM " + ENERGY_LOG_TABLE +
                               " WHERE DeviceName = '" + escapedDeviceName + "' AND Timestamp >= '" + startDateStr + "' AND Timestamp < '" + endDateStr + "'";

                object[,] results2;
                string[] columns2;
                commonStore.Query(query2, out columns2, out results2);

                if (results2 != null && results2.GetLength(0) > 0)
                {
                    if (results2[0, 0] != DBNull.Value && results2[0, 1] != DBNull.Value)
                        totalKVARh += (Convert.ToDouble(results2[0, 0]) - Convert.ToDouble(results2[0, 1]));
                    if (results2[0, 2] != DBNull.Value && results2[0, 3] != DBNull.Value)
                        totalKVAh += (Convert.ToDouble(results2[0, 2]) - Convert.ToDouble(results2[0, 3]));
                }

                // Peak demand
                string query3 = "SELECT MAX(kW_Demand) as peakKW FROM " + ENERGY_LOG_TABLE +
                               " WHERE DeviceName = '" + escapedDeviceName + "' AND Timestamp >= '" + startDateStr + "' AND Timestamp < '" + endDateStr + "'";

                object[,] results3;
                string[] columns3;
                commonStore.Query(query3, out columns3, out results3);

                if (results3 != null && results3.GetLength(0) > 0 && results3[0, 0] != DBNull.Value)
                {
                    double peak = Convert.ToDouble(results3[0, 0]);
                    if (peak > peakDemandKW) peakDemandKW = peak;
                }

                // Power factor
                string query4 = "SELECT AVG(Demand_PF) as avgPF FROM " + ENERGY_LOG_TABLE +
                               " WHERE DeviceName = '" + escapedDeviceName + "' AND Timestamp >= '" + startDateStr + "' AND Timestamp < '" + endDateStr + "'";

                object[,] results4;
                string[] columns4;
                commonStore.Query(query4, out columns4, out results4);

                if (results4 != null && results4.GetLength(0) > 0 && results4[0, 0] != DBNull.Value)
                {
                    avgPowerFactor += Convert.ToDouble(results4[0, 0]);
                    deviceCount++;
                }
            }

            if (deviceCount > 0) avgPowerFactor /= deviceCount;
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", "KPI error: " + ex.Message);
        }

        return new { totalEnergy = Math.Round(totalKWh, 2), peakDemand = Math.Round(peakDemandKW, 2), avgPowerFactor = Math.Round(avgPowerFactor, 3), reactiveEnergy = Math.Round(totalKVARh, 2), apparentEnergy = Math.Round(totalKVAh, 2) };
    }

    private object GetEnergyTrendData(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
    {
        var timestamps = new List<string>();
        var device1Data = new List<double>();
        var device2Data = new List<double>();

        try
        {
            if (deviceNames == null || deviceNames.Count == 0)
                return new { timestamps, device1KWh = device1Data, device2KWh = device2Data };

            string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            string endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            string deviceFilter = string.Join("','", deviceNames.Select(n => (n ?? "").Replace("'", "''")));

            string query = "SELECT Timestamp, DeviceName, kWh_Net FROM " + ENERGY_LOG_TABLE +
                          " WHERE DeviceName IN ('" + deviceFilter + "') AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" ORDER BY Timestamp ASC, DeviceName ASC";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results == null || results.GetLength(0) == 0)
                return new { timestamps, device1KWh = device1Data, device2KWh = device2Data };

            var slotAgg = new Dictionary<string, Dictionary<string, (double sum, int count)>>();
            var slotOrder = new SortedDictionary<DateTime, string>();

            for (int i = 0; i < results.GetLength(0); i++)
            {
                string dev = results[i, 1]?.ToString() ?? "";
                if (string.IsNullOrEmpty(dev)) continue;

                DateTime ts;
                object tsObj = results[i, 0];
                if (tsObj is DateTime dt) ts = dt;
                else
                {
                    var tsStr = tsObj?.ToString() ?? "";
                    if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                        !DateTime.TryParse(tsStr, out ts)) continue;
                }

                string slotStr;
                DateTime slotKey;

                if (daysInRange <= 1)
                {
                    slotStr = ts.ToString("HH:mm", CultureInfo.InvariantCulture);
                    slotKey = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0);
                }
                else
                {
                    slotStr = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    slotKey = ts.Date;
                }

                if (!slotAgg.TryGetValue(slotStr, out var devMap))
                {
                    devMap = new Dictionary<string, (double sum, int count)>();
                    slotAgg[slotStr] = devMap;
                    if (!slotOrder.ContainsKey(slotKey)) slotOrder[slotKey] = slotStr;
                }

                double kwh = (results[i, 2] != DBNull.Value && results[i, 2] != null) ? Convert.ToDouble(results[i, 2]) : 0.0;
                if (!devMap.TryGetValue(dev, out var agg)) agg = (0.0, 0);
                agg.sum += kwh;
                agg.count += 1;
                devMap[dev] = agg;
            }

            foreach (var kvp in slotOrder)
            {
                string slot = kvp.Value;
                timestamps.Add(slot);

                var d1 = deviceNames[0];
                if (slotAgg.TryGetValue(slot, out var devMap) && devMap.TryGetValue(d1, out var a1) && a1.count > 0)
                    device1Data.Add(a1.sum / a1.count);
                else
                    device1Data.Add(0);

                if (deviceNames.Count > 1)
                {
                    var d2 = deviceNames[1];
                    if (slotAgg.TryGetValue(slot, out devMap) && devMap.TryGetValue(d2, out var a2) && a2.count > 0)
                        device2Data.Add(a2.sum / a2.count);
                    else
                        device2Data.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error collecting energy trend: {ex.Message}");
        }

        return new { timestamps, device1KWh = device1Data, device2KWh = device2Data };
    }

    private object GetDeviceEnergyDistribution(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var devices = new List<object>();

        try
        {
            // Use ISO format to match Dashboard
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");

            foreach (var deviceName in deviceNames)
            {
                string escapedDeviceName = deviceName.Replace("'", "''");
                // Use Total_kWh_Net to match Dashboard
                string query = "SELECT MAX(Total_kWh_Net) as MaxVal, MIN(Total_kWh_Net) as MinVal FROM " + ENERGY_LOG_TABLE +
                              " WHERE DeviceName = '" + escapedDeviceName + "' AND Timestamp >= '" + startDateStr + "' AND Timestamp < '" + endDateStr + "'";

                object[,] results;
                string[] columns;
                commonStore.Query(query, out columns, out results);

                double totalKWh = 0;
                if (results != null && results.GetLength(0) > 0 && results[0, 0] != DBNull.Value && results[0, 1] != DBNull.Value)
                    totalKWh = Convert.ToDouble(results[0, 0]) - Convert.ToDouble(results[0, 1]);

                devices.Add(new { name = deviceName, value = Math.Round(totalKWh, 2) });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error collecting device energy distribution: {ex.Message}");
        }

        return new { devices };
    }

    private object GetDemandProfileData(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
    {
        var timestamps = new List<string>();
        var kWDemand = new List<double>();

        try
        {
            // Use ISO format to match Dashboard
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");

            string query = "SELECT Timestamp, kW_Demand FROM " + ENERGY_LOG_TABLE +
                          " WHERE Timestamp >= '" + startDateStr + "' AND Timestamp < '" + endDateStr + "' ORDER BY Timestamp";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results != null && results.GetLength(0) > 0)
            {
                int sampleRate = Math.Max(1, results.GetLength(0) / 20);
                for (int i = 0; i < results.GetLength(0); i += sampleRate)
                {
                    string ts = results[i, 0]?.ToString() ?? "";
                    timestamps.Add(ts.Length >= 5 ? ts.Substring(Math.Max(0, ts.Length - 5)) : ts);
                    kWDemand.Add(results[i, 1] != DBNull.Value ? Convert.ToDouble(results[i, 1]) : 0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error collecting demand profile: {ex.Message}");
        }

        return new { timestamps, kWDemand };
    }

    private object GetActiveReactiveData(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var labels = new List<string>();
        var activeEnergy = new List<double>();

        try
        {
            if (deviceNames == null || deviceNames.Count == 0)
                return new { labels, activeEnergy };

            string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            string endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

            var safeNames = deviceNames.Where(n => !string.IsNullOrEmpty(n)).Select(n => n.Replace("'", "''")).ToList();
            if (safeNames.Count == 0) return new { labels, activeEnergy };

            string deviceFilter = string.Join("','", safeNames);
            string query = "SELECT DeviceName, MAX(kWh_Net) AS MaxKWh, MIN(kWh_Net) AS MinKWh FROM " + ENERGY_LOG_TABLE +
                          " WHERE DeviceName IN ('" + deviceFilter + "') AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" GROUP BY DeviceName";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results != null && results.GetLength(0) > 0)
            {
                for (int i = 0; i < results.GetLength(0); i++)
                {
                    string dev = results[i, 0]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(dev)) continue;

                    double maxKWh = (results[i, 1] != DBNull.Value) ? Convert.ToDouble(results[i, 1]) : 0.0;
                    double minKWh = (results[i, 2] != DBNull.Value) ? Convert.ToDouble(results[i, 2]) : 0.0;

                    labels.Add(dev);
                    activeEnergy.Add(Math.Round(maxKWh - minKWh, 2));
                }
            }

            labels.Add("Total");
            activeEnergy.Add(Math.Round(activeEnergy.Sum(), 2));
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error collecting active/reactive data: {ex.Message}");
        }

        return new { labels, activeEnergy };
    }

    private object GetPowerFactorData(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
    {
        var timestamps = new List<string>();
        var device1PF = new List<double>();
        const double DefaultPF = 0.95;

        try
        {
            if (deviceNames == null || deviceNames.Count == 0)
            {
                timestamps.Add(startDate.ToString("yyyy-MM-dd"));
                device1PF.Add(DefaultPF);
                return new { timestamps, device1PF };
            }

            string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            string endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

            var safeNames = deviceNames.Where(n => !string.IsNullOrEmpty(n)).Select(n => n.Replace("'", "''")).ToList();
            if (safeNames.Count == 0)
            {
                timestamps.Add(startDate.ToString("yyyy-MM-dd"));
                device1PF.Add(DefaultPF);
                return new { timestamps, device1PF };
            }

            string deviceFilter = string.Join("','", safeNames);
            string query = "SELECT Timestamp, DeviceName, Demand_PF FROM " + ENERGY_LOG_TABLE +
                          " WHERE DeviceName IN ('" + deviceFilter + "') AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" ORDER BY Timestamp ASC";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results == null || results.GetLength(0) == 0)
            {
                timestamps.Add(startDate.ToString("yyyy-MM-dd"));
                device1PF.Add(DefaultPF);
                return new { timestamps, device1PF };
            }

            var slotAgg = new Dictionary<string, Dictionary<string, (double sum, int count)>>();
            var slotOrder = new SortedDictionary<DateTime, string>();

            for (int i = 0; i < results.GetLength(0); i++)
            {
                string dev = results[i, 1]?.ToString() ?? "";
                if (string.IsNullOrEmpty(dev)) continue;

                DateTime ts;
                object tsObj = results[i, 0];
                if (tsObj is DateTime dt) ts = dt;
                else
                {
                    var tsStr = tsObj?.ToString() ?? "";
                    if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                        !DateTime.TryParse(tsStr, out ts)) continue;
                }

                string slotStr = daysInRange <= 1 ? ts.ToString("HH:mm", CultureInfo.InvariantCulture) : ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DateTime slotKey = daysInRange <= 1 ? new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0) : ts.Date;

                if (!slotAgg.TryGetValue(slotStr, out var devMap))
                {
                    devMap = new Dictionary<string, (double sum, int count)>();
                    slotAgg[slotStr] = devMap;
                    if (!slotOrder.ContainsKey(slotKey)) slotOrder[slotKey] = slotStr;
                }

                double pf = (results[i, 2] != DBNull.Value) ? Convert.ToDouble(results[i, 2]) : DefaultPF;
                if (!devMap.TryGetValue(dev, out var a)) a = (0.0, 0);
                a.sum += pf;
                a.count += 1;
                devMap[dev] = a;
            }

            string d1 = deviceNames[0];
            foreach (var kvp in slotOrder)
            {
                string slot = kvp.Value;
                timestamps.Add(slot);

                if (slotAgg.TryGetValue(slot, out var devMap) && devMap.TryGetValue(d1, out var a1) && a1.count > 0)
                    device1PF.Add(Math.Round(a1.sum / a1.count, 3));
                else
                    device1PF.Add(DefaultPF);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error collecting power factor data: {ex.Message}");
        }

        return new { timestamps, device1PF };
    }

    private object GetDeviceStatusDistribution(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var labels = new List<string>();
        var status1 = new List<int>();

        try
        {
            string startDateStr = startDate.ToString("yyyy-MM-dd HH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var deviceName in deviceNames)
            {
                labels.Add(deviceName);
                string escapedDeviceName = deviceName.Replace("'", "''");

                string query = "SELECT SUM(Status_1_Count_x1) as Status1 FROM " + ENERGY_LOG_TABLE +
                              " WHERE DeviceName = '" + escapedDeviceName + "' AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";

                object[,] results;
                string[] columns;
                commonStore.Query(query, out columns, out results);

                status1.Add(results != null && results.GetLength(0) > 0 && results[0, 0] != DBNull.Value ? Convert.ToInt32(results[0, 0]) : 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error collecting device status distribution: {ex.Message}");
        }

        return new { labels, status1 };
    }

    // ==================== HELPER METHODS ====================

    private List<string> GetDeviceNamesFromOrgNode(string orgNodePath)
    {
        var devices = new List<string>();

        try
        {
            if (string.IsNullOrEmpty(orgNodePath)) return devices;

            var orgNode = Project.Current.Get(orgNodePath);
            if (orgNode == null)
            {
                Log.Warning("ReportGeneratorLogic", $"Org node not found: {orgNodePath}");
                return devices;
            }

            var deviceNodes = new List<IUANode>();
            CollectDeviceNodes(orgNode, deviceNodes);

            foreach (var deviceNode in deviceNodes)
            {
                if (deviceNode != null && !devices.Contains(deviceNode.BrowseName))
                    devices.Add(deviceNode.BrowseName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportGeneratorLogic", $"Error getting device names from org node {orgNodePath}: {ex.Message}");
        }

        return devices;
    }

    private void CollectDeviceNodes(IUANode node, List<IUANode> devices)
    {
        if (node.GetObject("DeviceAlarms") != null && node.GetObject("DeviceStatus") != null)
            devices.Add(node);

        foreach (var child in node.Children)
            CollectDeviceNodes(child, devices);
    }

    private DateTime CalculateNextRunTime(string frequency, string timeOfDay, int dayOfWeek, int dayOfMonth)
    {
        try
        {
            var time = DateTime.Parse(timeOfDay);
            var now = DateTime.Now;
            DateTime nextRun;

            switch (frequency.ToLower())
            {
                case "daily":
                    nextRun = new DateTime(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0);
                    if (nextRun <= now)
                        nextRun = nextRun.AddDays(1);
                    break;

                case "weekly":
                    // dayOfWeek: 1=Monday, 7=Sunday
                    nextRun = new DateTime(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0);
                    int currentDayOfWeek = (int)now.DayOfWeek; // 0=Sunday, 6=Saturday
                    if (currentDayOfWeek == 0) currentDayOfWeek = 7; // Adjust Sunday to 7
                    
                    int daysUntilTarget = dayOfWeek - currentDayOfWeek;
                    if (daysUntilTarget < 0 || (daysUntilTarget == 0 && nextRun <= now))
                        daysUntilTarget += 7;
                    
                    nextRun = nextRun.AddDays(daysUntilTarget);
                    break;

                case "monthly":
                    // dayOfMonth: 1-31
                    nextRun = new DateTime(now.Year, now.Month, Math.Min(dayOfMonth, DateTime.DaysInMonth(now.Year, now.Month)), time.Hour, time.Minute, 0);
                    
                    if (nextRun <= now)
                    {
                        // Move to next month
                        var nextMonth = now.AddMonths(1);
                        int validDay = Math.Min(dayOfMonth, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
                        nextRun = new DateTime(nextMonth.Year, nextMonth.Month, validDay, time.Hour, time.Minute, 0);
                    }
                    break;

                default:
                    nextRun = new DateTime(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0).AddDays(1);
                    break;
            }

            return nextRun;
        }
        catch
        {
            return DateTime.Now.Date.AddDays(1).AddHours(9);
        }
    }

    private string StripModelPrefix(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        const string prefix = "Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel/";
        if (fullPath.StartsWith(prefix)) return fullPath.Substring(prefix.Length);
        return fullPath;
    }

    private string SanitizeForFilename(string input)
    {
        if (string.IsNullOrEmpty(input)) return "Unknown";
        string invalid = new string(Path.GetInvalidFileNameChars());
        foreach (char c in invalid) input = input.Replace(c, '_');
        return input.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }

    private string GetTimeRangeDescription(int days)
    {
        if (days == 1) return "Last 24 Hours";
        if (days == 7) return "Last 7 Days";
        if (days == 30) return "Last 30 Days";
        return $"Last {days} Days";
    }

    /// <summary>
    /// Reads the storage path from configuration variable with fallback to default
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
            
            Log.Warning("ReportGeneratorLogic", $"{variableName} not found or empty. Using default: {defaultPath}");
            return ResolvePathShortcut(defaultPath);
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Error reading {variableName}: {ex.Message}. Using default: {defaultPath}");
            return ResolvePathShortcut(defaultPath);
        }
    }

    /// <summary>
    /// Resolves path shortcuts like %USB1%, %APPLICATIONDIR%, %PROJECTDIR% to actual paths
    /// </summary>
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
                        Log.Info("ReportGeneratorLogic", $"Resolved USB path: {pathShortcut} -> {usbUri}");
                        return usbUri;
                    }
                    catch (Exception usbEx)
                    {
                        Log.Error("ReportGeneratorLogic", $"USB{usbNumber} not available: {usbEx.Message}. Falling back to %APPLICATIONDIR%");
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
            Log.Warning("ReportGeneratorLogic", $"Unknown path format '{pathShortcut}', using as literal path");
            return pathShortcut;
        }
        catch (Exception ex)
        {
            Log.Error("ReportGeneratorLogic", $"Failed to resolve path '{pathShortcut}': {ex.Message}");
            return new ResourceUri("%APPLICATIONDIR%\\").Uri;
        }
    }
}
