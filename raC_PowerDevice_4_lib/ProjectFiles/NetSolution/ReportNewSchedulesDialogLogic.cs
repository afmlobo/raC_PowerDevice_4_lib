#region Using directives
using System;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.UI;
using FTOptix.DataLogger;
using FTOptix.System;
using FTOptix.SerialPort;
#endregion

/// <summary>
/// Runtime NetLogic for Report New Schedules Dialog
/// Handles form validation and passes data to ReportsPanelLogic
/// </summary>
public class ReportNewSchedulesDialogLogic : BaseNetLogic
{
    private TextBox scheduleNameTextBox;
    private Label locationLabel;
    private RadioButton frequencyDaily;
    private RadioButton frequencyWeekly;
    private RadioButton frequencyMonthly;
    private CheckBox isActiveCheckbox;
    private Label errorLabel;
    private SpinBox dayOfWeekSpinBox;
    private SpinBox dayOfMonthSpinBox;

    private IUAVariable selectedTimeVariable;
    private string fullOrgPath = ""; // Store full path for saving

    public override void Start()
    {
        try
        {
            // Find all controls in the dialog
            scheduleNameTextBox = Owner.Get<TextBox>("VerticalLayout1/GenerateReportContainer/tb_ScheduleName");
            locationLabel = Owner.Get<Label>("VerticalLayout1/GenerateReportContainer/HorizontalLayout2/PathLabel");
            frequencyDaily = Owner.Get<RadioButton>("VerticalLayout1/GenerateReportContainer/Daily");
            frequencyWeekly = Owner.Get<RadioButton>("VerticalLayout1/GenerateReportContainer/Weekly");
            frequencyMonthly = Owner.Get<RadioButton>("VerticalLayout1/GenerateReportContainer/Monthly");
            isActiveCheckbox = Owner.Get<CheckBox>("VerticalLayout1/GenerateReportContainer/cbIsActiveCheckBox");
            errorLabel = Owner.Get<Label>("VerticalLayout1/GenerateReportContainer/ErrorLabel");
            dayOfWeekSpinBox = Owner.Get<SpinBox>("VerticalLayout1/GenerateReportContainer/hl_DayOfWeek/sb_DayOfWeek");
            dayOfMonthSpinBox = Owner.Get<SpinBox>("VerticalLayout1/GenerateReportContainer/hl_DayOfMonth/sb_DayOfMonth");
            selectedTimeVariable = Owner.GetVariable("VerticalLayout1/GenerateReportContainer/SelectedTime");

            // Auto-populate location from alias
            try
            {
                var currentPathAlias = Project.Current.GetVariable("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/CurrentSelectedOrgPath");
                if (currentPathAlias != null)
                {
                    string currentPath = currentPathAlias.Value;
                    if (!string.IsNullOrEmpty(currentPath))
                    {
                        fullOrgPath = currentPath; // Store full path
                        if (locationLabel != null)
                            locationLabel.Text = StripOrgModelPrefix(currentPath);
                    }
                }
                else if (locationLabel != null)
                {
                    locationLabel.Text = "No location selected";
                    fullOrgPath = "";
                }
            }
            catch (Exception ex)
            {
                Log.Warning("ReportNewSchedulesDialogLogic", $"Error reading CurrentPath: {ex.Message}");
                if (locationLabel != null)
                    locationLabel.Text = "No location selected";
            }

            // Set default values
            if (frequencyDaily != null)
                frequencyDaily.Checked = true;

            if (selectedTimeVariable != null)
            {
                var currentTime = selectedTimeVariable.Value;
                if (currentTime == null || (DateTime)currentTime == DateTime.MinValue)
                    selectedTimeVariable.Value = new DateTime(1, 1, 1, 9, 0, 0); // Default to 09:00
            }

            if (isActiveCheckbox != null)
                isActiveCheckbox.Checked = true;

            if (dayOfWeekSpinBox != null)
                dayOfWeekSpinBox.Value = 1; // Default Monday

            if (dayOfMonthSpinBox != null)
                dayOfMonthSpinBox.Value = 1; // Default 1st of month

            if (errorLabel != null)
                errorLabel.Text = "";

            // Clear schedule name for new entry
            if (scheduleNameTextBox != null)
                scheduleNameTextBox.Text = "";
        }
        catch (Exception ex)
        {
            Log.Error("ReportNewSchedulesDialogLogic", $"Start error: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when Save button is clicked
    /// Creates schedule instance and saves directly to Schedules folder
    /// </summary>
    [ExportMethod]
    public void SaveSchedule()
    {
        try
        {
            if (!ValidateInputs())
                return;

            // Get full location path (not the display version)
            string orgPath = fullOrgPath;
            
            // Get frequency from radio buttons
            string frequency = "Daily";
            if (frequencyWeekly?.Checked == true)
                frequency = "Weekly";
            else if (frequencyMonthly?.Checked == true)
                frequency = "Monthly";

            // Calculate report period automatically based on frequency
            string reportPeriod = GetReportPeriodFromFrequency(frequency);

            // Get time from DateTime variable
            string timeOfDay = "09:00"; // Default
            if (selectedTimeVariable != null && selectedTimeVariable.Value != null)
            {
                var timeValue = (System.DateTime)selectedTimeVariable.Value;
                timeOfDay = $"{timeValue.Hour:D2}:{timeValue.Minute:D2}";
            }
            
            bool isActive = isActiveCheckbox?.Checked ?? true;

            // Get day values from spinboxes
            int dayOfWeek = (int)(dayOfWeekSpinBox?.Value ?? 1); // 1-7 (Monday-Sunday)
            int dayOfMonth = (int)(dayOfMonthSpinBox?.Value ?? 1); // 1-31

            // Generate dynamic filename template to prevent conflicts
            string sanitizedOrgPath = SanitizeForFilename(orgPath);
            string fileNameTemplate = $"Report_{sanitizedOrgPath}_{frequency}_{{Date}}.pdf";

            // Get Schedules folder
            var schedulesFolder = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ReportSchedules");
                                                       
            if (schedulesFolder == null)
            {
                Log.Error("ReportNewSchedulesDialogLogic", "Schedules folder not found");
                ShowError("Schedules folder not found!");
                return;
            }

            // Get the ScheduleType
            var scheduleType = Project.Current.Find("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ScheduleType");
            if (scheduleType == null)
            {
                Log.Error("ReportNewSchedulesDialogLogic", "ScheduleType not found");
                ShowError("ScheduleType not found!");
                return;
            }

            // Create a unique schedule ID
            string scheduleId = "Schedule_" + Guid.NewGuid().ToString().Substring(0, 8);

            // Get user-entered schedule name (fallback to scheduleId if empty)
            string scheduleName = scheduleNameTextBox?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(scheduleName))
                scheduleName = scheduleId;

            // Create instance from type
            var scheduleInstance = InformationModel.MakeObject(scheduleId, scheduleType.NodeId);

            // Set all properties
            scheduleInstance.GetVariable("ScheduleId").Value = scheduleId;
            
            // Set user-friendly name (shown in grid)
            var scheduleNameVar = scheduleInstance.GetVariable("ScheduleName");
            if (scheduleNameVar != null)
                scheduleNameVar.Value = scheduleName;
            scheduleInstance.GetVariable("LocationPath").Value = orgPath;
            scheduleInstance.GetVariable("Frequency").Value = frequency;
            
            // Set DayOfWeek and DayOfMonth based on frequency
            if (frequency == "Weekly")
            {
                scheduleInstance.GetVariable("DayOfWeek").Value = dayOfWeek; // 1-7 for Weekly
                scheduleInstance.GetVariable("DayOfMonth").Value = 0; // Not applicable
            }
            else if (frequency == "Monthly")
            {
                scheduleInstance.GetVariable("DayOfWeek").Value = 0; // Not applicable
                scheduleInstance.GetVariable("DayOfMonth").Value = dayOfMonth; // 1-31 for Monthly
            }
            else // Daily
            {
                scheduleInstance.GetVariable("DayOfWeek").Value = 0; // Not applicable
                scheduleInstance.GetVariable("DayOfMonth").Value = 0; // Not applicable
            }
            
            scheduleInstance.GetVariable("TimeOfDay").Value = timeOfDay;
            scheduleInstance.GetVariable("ReportPeriod").Value = reportPeriod;
            scheduleInstance.GetVariable("FileNameTemplate").Value = fileNameTemplate;
            scheduleInstance.GetVariable("EmailTo").Value = "";
            scheduleInstance.GetVariable("IsActive").Value = isActive;
            
            // Calculate next run time
            DateTime nextRun = CalculateNextRunTime(frequency, timeOfDay, dayOfWeek, dayOfMonth);
            
            // Set NextRun with error checking
            var nextRunVar = scheduleInstance.GetVariable("NextRun");
            if (nextRunVar != null)
            {
                nextRunVar.Value = nextRun;
            }
            else
            {
                Log.Error("ReportNewSchedulesDialogLogic", "NextRun variable does not exist on ScheduleType! You must add NextRun (DateTime) variable to raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ScheduleType in FactoryTalk Optix.");
                ShowError("ERROR: NextRun variable missing from ScheduleType. Check logs.");
                return;
            }
            
            scheduleInstance.GetVariable("LastRun").Value = DateTime.MinValue;
            scheduleInstance.GetVariable("CreatedAt").Value = DateTime.Now;
            scheduleInstance.GetVariable("CreatedBy").Value = "User";

            // Add to schedules folder
            schedulesFolder.Add(scheduleInstance);

            if (errorLabel != null)
                errorLabel.Text = "[Done] Schedule saved!";
        }
        catch (Exception ex)
        {
            Log.Error("ReportNewSchedulesDialogLogic", $"SaveSchedule error: {ex.Message}\n{ex.StackTrace}");
            ShowError($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Calculate next run time based on frequency, time of day, and day parameters
    /// </summary>
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
                    int daysInCurrentMonth = DateTime.DaysInMonth(now.Year, now.Month);
                    int validDayCurrentMonth = Math.Min(dayOfMonth, daysInCurrentMonth);
                    nextRun = new DateTime(now.Year, now.Month, validDayCurrentMonth, time.Hour, time.Minute, 0);
                    
                    if (nextRun <= now)
                    {
                        // Move to next month
                        var nextMonth = now.AddMonths(1);
                        int daysInNextMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
                        int validDay = Math.Min(dayOfMonth, daysInNextMonth);
                        nextRun = new DateTime(nextMonth.Year, nextMonth.Month, validDay, time.Hour, time.Minute, 0);
                    }
                    break;

                default:
                    nextRun = new DateTime(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0).AddDays(1);
                    break;
            }

            return nextRun;
        }
        catch (Exception ex)
        {
            // If parsing fails, default to tomorrow at 9 AM
            Log.Error("ReportNewSchedulesDialogLogic", $"CalculateNextRunTime error: {ex.Message}");
            return DateTime.Now.Date.AddDays(1).AddHours(9);
        }
    }

    /// <summary>
    /// Calculate report period automatically based on frequency
    /// </summary>
    private string GetReportPeriodFromFrequency(string frequency)
    {
        switch (frequency.ToLower())
        {
            case "daily":
                return "Last24Hours";
            case "weekly":
                return "Last7Days";
            case "monthly":
                return "Last30Days";
            default:
                return "Last30Days";
        }
    }

    /// <summary>
    /// Validate form inputs
    /// </summary>
    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(fullOrgPath))
        {
            ShowError("Please select a location");
            return false;
        }

        if (selectedTimeVariable == null || selectedTimeVariable.Value == null)
        {
            ShowError("Please select a time");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sanitize org path for use in filename
    /// </summary>
    private string SanitizeForFilename(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "Unknown";

        string invalid = new string(System.IO.Path.GetInvalidFileNameChars());
        foreach (char c in invalid)
            input = input.Replace(c, '_');

        return input.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
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

    private void ShowError(string message)
    {
        if (errorLabel != null)
            errorLabel.Text = message;
        
        Log.Warning("ReportNewSchedulesDialogLogic", message);
    }
}
