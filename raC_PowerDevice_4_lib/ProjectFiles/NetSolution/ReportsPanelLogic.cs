#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.UI;
using FTOptix.DataLogger;
using FTOptix.Retentivity;
using FTOptix.SerialPort;
#endregion

/// <summary>
/// Runtime NetLogic for Reports Panel
/// Handles Generate Now, Schedule Report, and displaying schedules
/// </summary>
public class ReportsPanelLogic : BaseNetLogic
{
    private IUAVariable currentOrgPathVar;
    private IUAVariable currentPathVariable;
    private IUANode schedulesFolder;
    private DataGrid schedulesDataGrid;

    private Label locationLabel;
    private Label statusLabel;
    private DelayedTask statusHideTask;
    private DateTime statusMessageTime;

    public override void Start()
    {
        try
        {
            var navigationPanel = Owner.Owner;
            currentOrgPathVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            locationLabel = Owner.Get<Label>("VerticalLayout1/HeaderRectangle/HorizontalLayout1/CurrentLocationLabel");
            statusLabel = Owner.Get<Label>("VerticalLayout1/HorizontalLayout1/GenerateReportContainer/StatusLabel");

            if (currentOrgPathVar == null)
            {
                Log.Warning("ReportsPanelLogic", "SelectedOrgNodePath variable not found");
            }
            else
            {
                currentOrgPathVar.VariableChange += OnSelectedOrgNodeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportsPanelLogic", $"Error getting SelectedOrgNodePath: {ex.Message}");
        }
        
        try
        {
            var modelNode = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model");
            if (modelNode != null)
            {
                currentPathVariable = modelNode.GetVariable("CurrentSelectedOrgPath");
                if (currentPathVariable == null)
                {
                    currentPathVariable = InformationModel.MakeVariable("CurrentSelectedOrgPath", OpcUa.DataTypes.String);
                    modelNode.Add(currentPathVariable);
                }
                
                string currentPath = GetCurrentOrgPath();
                currentPathVariable.Value = currentPath;
                if (locationLabel != null)
                    locationLabel.Text = StripOrgModelPrefix(currentPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("ReportsPanelLogic", $"Error setting up CurrentSelectedOrgPath: {ex.Message}");
        }
        
        schedulesFolder = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ReportSchedules");
        
        if (schedulesFolder == null)
        {
            Log.Warning("ReportsPanelLogic", "Schedules folder not found, creating it");
            schedulesFolder = InformationModel.Make<Folder>("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ReportSchedules");
            var dashboardModel = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model");
            if (dashboardModel != null)
                dashboardModel.Add(schedulesFolder);
        }

        

        schedulesDataGrid = Owner.Find<DataGrid>("SchedulesDataGrid");
        if (schedulesDataGrid == null)
        {
            schedulesDataGrid = Owner.Find<DataGrid>(null);
        }

        if (schedulesDataGrid != null)
        {
            schedulesDataGrid.Model = schedulesFolder.NodeId;
        }
        else
        {
            Log.Warning("ReportsPanelLogic", "SchedulesDataGrid not found");
        }
    }

    public override void Stop()
    {
        // Unsubscribe from events
        if (currentOrgPathVar != null)
        {
            currentOrgPathVar.VariableChange -= OnSelectedOrgNodeChanged;
        }
        
        // Dispose delayed task if any
        if (statusHideTask != null)
        {
            statusHideTask.Dispose();
            statusHideTask = null;
        }
    }

    private void OnSelectedOrgNodeChanged(object sender, VariableChangeEventArgs e)
    {
        string selectedPath = currentOrgPathVar?.Value ?? "";
        
        if (currentPathVariable != null)
        {
            currentPathVariable.Value = selectedPath;
            if (locationLabel != null)
                locationLabel.Text = StripOrgModelPrefix(selectedPath);
        }
    }

    /// <summary>
    /// Called when "Generate Now" button is clicked
    /// Triggers manual report generation via ReportGeneratorLogic
    /// </summary>
    [ExportMethod]
    public void GenerateReportNow()
    {
        try
        {
            // Show generating message
            if (statusLabel != null)
                statusLabel.Text = "[GENERATING] Generating report...";

            string orgPath = GetCurrentOrgPath();
            if (string.IsNullOrEmpty(orgPath))
            {
                ShowStatusMessage("[ERROR] No organization path selected");
                Log.Error("ReportsPanelLogic", "No organization path selected");
                return;
            }

            string reportPeriod = GetSelectedReportPeriod();

            var reportGenerator = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/ReportGeneratorLogic");
            if (reportGenerator == null)
            {
                ShowStatusMessage("[ERROR] ReportGeneratorLogic not found");
                Log.Error("ReportsPanelLogic", "ReportGeneratorLogic not found in Model");
                return;
            }

            ((IUAObject)reportGenerator).ExecuteMethod("GenerateManualReport", new object[] { orgPath, reportPeriod });
            
            // Show success message
            ShowStatusMessage("[OK] Report generated successfully!");
        }
        catch (Exception ex)
        {
            ShowStatusMessage($"[ERROR] {ex.Message}");
            Log.Error("ReportsPanelLogic", $"Error triggering report generation: {ex.Message}");
        }
    }

    /// <summary>
    /// Open Schedule Dialog - called from UI button
    /// Pre-populates with current org path via alias
    /// </summary>
    [ExportMethod]
    public void OpenScheduleDialog()
    {
        try
        {
            // Dialog is opened via UI binding
        }
        catch (Exception ex)
        {
            Log.Error("ReportsPanelLogic", $"Error opening dialog: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete a schedule from the grid
    /// </summary>
    [ExportMethod]
    public void DeleteSchedule()
    {
        try
        {
            if (schedulesDataGrid == null)
            {
                Log.Warning("ReportsPanelLogic", "SchedulesDataGrid not available");
                return;
            }

            var selectedItemVar = schedulesDataGrid.GetVariable("UISelectedItem");
            if (selectedItemVar == null)
            {
                Log.Warning("ReportsPanelLogic", "SelectedItem variable not found");
                return;
            }

            var selectedItemId = selectedItemVar.Value;
            if (selectedItemId == null)
            {
                Log.Warning("ReportsPanelLogic", "No schedule selected");
                return;
            }

            var nodeToDelete = InformationModel.Get<raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ScheduleType>(selectedItemId);
            if (nodeToDelete == null)
            {
                Log.Warning("ReportsPanelLogic", "Schedule node not found");
                return;
            }

            schedulesFolder.Remove(nodeToDelete);
            schedulesDataGrid.Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("ReportsPanelLogic", $"Error deleting schedule: {ex.Message}");
        }
    }

    // ==================== HELPER METHODS ====================

    private void ShowStatusMessage(string message)
    {
        if (statusLabel == null)
            return;

        // Cancel any existing hide task
        if (statusHideTask != null)
        {
            statusHideTask.Dispose();
            statusHideTask = null;
        }

        // Set the message and timestamp
        statusLabel.Text = message;
        statusMessageTime = DateTime.Now;
        
        // Schedule hide after 5 seconds
        statusHideTask = new DelayedTask(() =>
        {
            ClearStatusMessage();
        }, 5000, LogicObject);
        statusHideTask.Start();
    }

    private void ClearStatusMessage()
    {
        // Only clear if 5 seconds have passed
        if ((DateTime.Now - statusMessageTime).TotalMilliseconds >= 4900)
        {
            if (statusLabel != null)
                statusLabel.Text = "";
        }
        statusHideTask = null;
    }

    private string GetCurrentOrgPath()
    {
        if (currentOrgPathVar != null)
        {
            string path = currentOrgPathVar.Value;
            if (!string.IsNullOrEmpty(path))
                return path;
        }
        return "";
    }

    private string GetSelectedReportPeriod()
    {
        // Get the selected radio button from the UI
        var radioLast24Hours = Owner.Get<RadioButton>("VerticalLayout1/HorizontalLayout1/GenerateReportContainer/RadioLast24Hours");
        var radioLast7Days = Owner.Get<RadioButton>("VerticalLayout1/HorizontalLayout1/GenerateReportContainer/RadioLast7Days");
        var radioLast30Days = Owner.Get<RadioButton>("VerticalLayout1/HorizontalLayout1/GenerateReportContainer/RadioLast30Days");
        var radioCurrentMonth = Owner.Get<RadioButton>("VerticalLayout1/HorizontalLayout1/GenerateReportContainer/RadioCurrentMonth");

        if (radioLast24Hours?.Checked == true)
            return "Last24Hours";
        else if (radioLast7Days?.Checked == true)
            return "Last7Days";
        else if (radioLast30Days?.Checked == true)
            return "Last30Days";
        else if (radioCurrentMonth?.Checked == true)
            return "CurrentMonth";

        return "Last30Days"; // Default
    }

    /// <summary>
    /// Strip the org model prefix for display purposes only
    /// </summary>
    private string StripOrgModelPrefix(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return fullPath;
        
        const string prefix = "Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel/";
        if (fullPath.StartsWith(prefix))
            return fullPath.Substring(prefix.Length);
        
        return fullPath;
    }
}
