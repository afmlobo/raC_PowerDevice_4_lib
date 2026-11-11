#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;
#endregion

/// <summary>
/// NetLogic for OrgNode navigation button
/// Place this NetLogic inside: raC_4_01_raC_Dvc_PM5000_PQEM_OrgNodeType_NavGraphical
/// </summary>
public class Dashboard_OrgNodeNavLogic : BaseNetLogic
{
    private Image expandIcon;
    private Button dashboardButton;
    private IUANode childrenContainer;
    private bool isExpanded = true; // Start expanded by default
    private bool containerSearched = false; // Flag to avoid repeated searches

    public override void Start()
    {
        // Get references to UI elements
        var horizontalLayout = Owner.Get<RowLayout>("HorizontalLayout1");
        if (horizontalLayout == null)
        {
            Log.Error("Dashboard_OrgNodeNavLogic", "HorizontalLayout1 not found.");
            return;
        }

        // Get the expand/collapse icon (left side)
        expandIcon = horizontalLayout.Get<Image>("Image1");
        
        if (expandIcon == null)
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", "Expand icon 'Image1' not found.");
        }
        else
        {
            expandIcon.UAEvent += ExpandIcon_UAEvent;            
        }

        // Get the dashboard button
        dashboardButton = horizontalLayout.Get<Button>("DashboardButton");
        if (dashboardButton != null)
        {
            dashboardButton.UAEvent += DashboardButton_UAEvent;
        }
        else
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", "DashboardButton not found.");
        }

        // Set the title from OrgNode
        UpdateTitleFromOrgNode();

        CreateButtonPathVariable();
        SubscribeToSelectionChanges();
    }

    public override void Stop()
    {
        // Unsubscribe from events to prevent memory leaks
        if (expandIcon != null)
            expandIcon.UAEvent -= ExpandIcon_UAEvent;
            
        if (dashboardButton != null)
            dashboardButton.UAEvent -= DashboardButton_UAEvent;

        try
    {
        var horizontalLayout = FindHorizontalLayoutDashboard();
        if (horizontalLayout != null)
        {
            var navigationPanel = horizontalLayout.Get<NavigationPanel>("NavigationPanelright");
            if (navigationPanel != null)
            {
                var selectedOrgPathVar = navigationPanel.GetVariable("SelectedOrgNodePath");
                if (selectedOrgPathVar != null)
                    selectedOrgPathVar.VariableChange -= SelectedOrgPath_VariableChange;
            }
        }
    }
    catch { }
    }

    /// <summary>
    /// Handle expand/collapse icon UAEvent
    /// </summary>
    private void ExpandIcon_UAEvent(object sender, UAEventArgs e)
    {
        // ONLY handle MouseDownEvent, ignore MouseUpEvent
        if (e.EventType.BrowseName != "MouseDownEvent")
        {
            return;
        }
        
        // Search for children container if not already searched
        if (!containerSearched)
        {
            FindChildrenContainer();
            containerSearched = true;
            
            // If no children found, hide the icon permanently and return
            if (childrenContainer == null || !HasVisibleChildren())
            {
                Log.Warning("Dashboard_OrgNodeNavLogic", $"No children to expand/collapse for {Owner.BrowseName}");
                expandIcon.Visible = false; // Hide icon if no children
                return;
            }
        }
        
        // Toggle expanded state
        isExpanded = !isExpanded;
        
        // Update UI
        UpdateExpandIcon();
        SetChildrenVisibility(isExpanded);
        
        // Verify icon is still visible after update
        DelayedVerifyIconVisibility();
    }
    
    /// <summary>
    /// Verify icon visibility after a short delay (workaround for potential UI refresh issues)
    /// </summary>
    private void DelayedVerifyIconVisibility()
    {
        var delayedTask = new DelayedTask(VerifyIconVisibility, 100, LogicObject); // 100ms delay
        delayedTask.Start();
    }
    
    /// <summary>
    /// Verify and restore icon visibility if needed
    /// </summary>
    private void VerifyIconVisibility()
    {
        if (expandIcon == null) return;
        
        if (!expandIcon.Visible)
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", $"Icon was hidden! Restoring visibility for {Owner.BrowseName}");
            expandIcon.Visible = true;
        }
    }

    /// <summary>
    /// Handle dashboard button UAEvent
    /// </summary>
    private void DashboardButton_UAEvent(object sender, UAEventArgs e)
    {
        // Check if this is a MouseUp event
        if (e.EventType.BrowseName == "MouseDownEvent")
        {
            HandleDashboardClick();
        }
    }

    /// <summary>
    /// Common handler for dashboard click
    /// UPDATED: Now integrates with DashboardAggregator
    /// </summary>
    private void HandleDashboardClick()
    {
        var orgNodePtr = Owner.GetVariable("OrgNode");
        if (orgNodePtr == null || orgNodePtr.Value.Value == null)
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", "OrgNode pointer is not set.");
            return;
        }

        try
        {
            NodeId orgNodeId = (NodeId)orgNodePtr.Value;
            var orgNode = InformationModel.Get(orgNodeId);
            
            if (orgNode == null)
            {
                Log.Error("Dashboard_OrgNodeNavLogic", "OrgNode not found.");
                return;
            }

            // Update DashboardAggregator with selected org node
            UpdateDashboardAggregator(orgNode);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_OrgNodeNavLogic", $"Error loading dashboard: {ex.Message}");
        }
    }

    /// <summary>
    /// NEW METHOD: Update DashboardAggregator with selected org node path
    /// </summary>
    private void UpdateDashboardAggregator(IUANode orgNode)
    {
        try
        {
            // Navigate directly to NavigationPanelright without relying on finding CommonDashboard
            var horizontalLayout = FindHorizontalLayoutDashboard();
            if (horizontalLayout == null)
            {
                Log.Error("Dashboard_OrgNodeNavLogic", "HorizontalLayout not found");
                return;
            }

            // Get NavigationPanelright by walking forward from HorizontalLayout
            var navigationPanel = horizontalLayout.Get<NavigationPanel>("NavigationPanelright");
            if (navigationPanel == null)
            {
                Log.Error("Dashboard_OrgNodeNavLogic", "NavigationPanelright not found");
                return;
            }

            // Get the full path of the org node
            string orgNodePath = GetNodePath(orgNode);
            
            // Get SelectedOrgNodePath variable from NavigationPanelright
            var selectedOrgPathVar = navigationPanel.GetVariable("SelectedOrgNodePath");
            if (selectedOrgPathVar == null)
            {
                Log.Warning("Dashboard_OrgNodeNavLogic", "SelectedOrgNodePath variable not found in NavigationPanelright");
                return;
            }

            // Update the shared variable - all subscribed aggregators will be notified
            selectedOrgPathVar.Value = orgNodePath;
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_OrgNodeNavLogic", $"Error updating DashboardAggregator: {ex.Message}");
        }
    }

    /// <summary>
    /// NEW METHOD: Get full path of a node in the information model
    /// </summary>
    private string GetNodePath(IUANode node)
    {
        var pathParts = new System.Collections.Generic.List<string>();
        var currentNode = node;
        
        // Walk up the tree to build the path
        while (currentNode != null && currentNode != Project.Current)
        {
            pathParts.Insert(0, currentNode.BrowseName);
            currentNode = currentNode.Owner;
        }
        
        return string.Join("/", pathParts);
    }

    /// <summary>
    /// Update the expand/collapse icon based on current state
    /// Uses rotation instead of changing icon paths (more reliable)
    /// </summary>
    private void UpdateExpandIcon()
    {
        if (expandIcon == null)
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", "UpdateExpandIcon: expandIcon is null");
            return;
        }

        // ALWAYS ensure icon is visible
        expandIcon.Visible = true;
        
        // Use rotation (RECOMMENDED - most reliable)
        if (isExpanded)
        {
            // Expanded state - rotate arrow down (90 degrees)
            expandIcon.Rotation = 90;
        }
        else
        {
            // Collapsed state - arrow pointing right (0 degrees)
            expandIcon.Rotation = 0;
        }
    }

    /// <summary>
    /// Show or hide children container
    /// </summary>
    private void SetChildrenVisibility(bool visible)
    {
        if (childrenContainer == null)
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", "Cannot set visibility - childrenContainer is null");
            return;
        }

        // Cast to ColumnLayout (or Item which has Visible property)
        var layout = childrenContainer as ColumnLayout;
        if (layout != null)
        {
            layout.Visible = visible;
            return;
        }

        // Fallback: try casting to Item (base class for UI elements)
        var item = childrenContainer as Item;
        if (item != null)
        {
            item.Visible = visible;
        }
        else
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", $"Could not cast childrenContainer to ColumnLayout or Item");
        }
    }

    /// <summary>
    /// Find the children container (created by TreeNavLogic)
    /// This is called LAZILY when first needed, not at startup
    /// </summary>
    private void FindChildrenContainer()
    {
        var parent = Owner.Owner;
        if (parent == null)
        {
            Log.Warning("Dashboard_OrgNodeNavLogic", $"Parent is null for {Owner.BrowseName}");
            return;
        }

        // Get the OrgNode this button represents
        var orgNodePtr = Owner.GetVariable("OrgNode");
        if (orgNodePtr != null && orgNodePtr.Value.Value != null)
        {
            try
            {
                NodeId orgNodeId = (NodeId)orgNodePtr.Value;
                var orgNode = InformationModel.Get(orgNodeId);
                if (orgNode != null)
                {
                    string expectedContainerName = $"Children_{orgNode.BrowseName}";

                    // Search for the container by exact name match
                    foreach (var sibling in parent.Children)
                    {
                        if (sibling.BrowseName == expectedContainerName)
                        {
                            childrenContainer = sibling;
                            return;
                        }
                    }
                    
                    Log.Warning("Dashboard_OrgNodeNavLogic", $"Container '{expectedContainerName}' not found in siblings");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Dashboard_OrgNodeNavLogic", $"Error finding children container: {ex.Message}");
            }
        }

        // Fallback: look for next sibling starting with "Children_"
        bool foundSelf = false;
        foreach (var sibling in parent.Children)
        {
            if (foundSelf && sibling.BrowseName.StartsWith("Children_"))
            {
                childrenContainer = sibling;
                return;
            }
            
            if (sibling == Owner)
            {
                foundSelf = true;
            }
        }
    }

    /// <summary>
    /// Check if children container has any visible child nodes
    /// </summary>
    private bool HasVisibleChildren()
    {
        if (childrenContainer == null)
        {
            return false;
        }

        int childCount = 0;
        foreach (var child in childrenContainer.Children)
        {
            // Only count Object nodes (not variables, etc.)
            if (child.NodeClass == NodeClass.Object)
            {
                childCount++;
            }
        }

        return childCount > 0;
    }

    /// <summary>
    /// Update the button text with OrgNode name
    /// </summary>
    private void UpdateTitleFromOrgNode()
    {
        if (dashboardButton == null) return;

        var orgNodePtr = Owner.GetVariable("OrgNode");
        if (orgNodePtr == null || orgNodePtr.Value.Value == null) return;

        try
        {
            NodeId orgNodeId = (NodeId)orgNodePtr.Value;
            var orgNode = InformationModel.Get(orgNodeId);
            
            if (orgNode != null)
            {
                string displayName = orgNode.BrowseName;
                dashboardButton.Text = displayName;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_OrgNodeNavLogic", $"Error setting title: {ex.Message}");
        }
    }

    /// <summary>
    /// Roll up from current node to find the HorizontalLayout Dashboard
    /// </summary>
    private IUANode FindHorizontalLayoutDashboard()
    {
        IUANode current = Owner;
        
        while (current != null)
        {
            if (current.BrowseName.Contains("HorizontalLayout") || current.BrowseName.Contains("hl_Dashboard"))
            {
                return current;
            }
            current = current.Owner;
        }

        Log.Warning("Dashboard_OrgNodeNavLogic", "HorizontalLayout Dashboard not found by rolling up");
        return null;
    }

    /// <summary>
/// Create a variable that stores THIS button's path (e.g., Model/.../Cell1)
/// </summary>
private void CreateButtonPathVariable()
{
    try
    {
        var orgNodePtr = Owner.GetVariable("OrgNode");
        if (orgNodePtr == null || orgNodePtr.Value.Value == null) return;

        NodeId orgNodeId = (NodeId)orgNodePtr.Value;
        var orgNode = InformationModel.Get(orgNodeId);
        
        if (orgNode != null)
        {
            string nodePath = GetNodePath(orgNode);
            
            // Store this button's path in a variable
            var buttonPathVar = LogicObject.GetVariable("ButtonPath") ?? 
                               InformationModel.MakeVariable("ButtonPath", OpcUa.DataTypes.String);
            if (LogicObject.GetVariable("ButtonPath") == null)
                LogicObject.Add(buttonPathVar);
            
            buttonPathVar.Value = nodePath;
        }
    }
    catch (Exception ex)
    {
        Log.Error("Dashboard_OrgNodeNavLogic", $"Error creating button path: {ex.Message}");
    }
}

/// <summary>
/// Subscribe to SelectedOrgNodePath changes from NavigationPanelright
/// When user clicks a button, this will be called automatically
/// </summary>
private void SubscribeToSelectionChanges()
{
    try
    {
        var horizontalLayout = FindHorizontalLayoutDashboard();
        if (horizontalLayout == null) return;

        var navigationPanel = horizontalLayout.Get<NavigationPanel>("NavigationPanelright");
        if (navigationPanel == null) return;

        var selectedOrgPathVar = navigationPanel.GetVariable("SelectedOrgNodePath");
        if (selectedOrgPathVar == null) return;

        // Subscribe to value changes
        selectedOrgPathVar.VariableChange += SelectedOrgPath_VariableChange;
    }
    catch (Exception ex)
    {
        Log.Error("Dashboard_OrgNodeNavLogic", $"Error subscribing to selection: {ex.Message}");
    }
}

/// <summary>
/// Called automatically when SelectedOrgNodePath changes
/// Compare: Does this button's path match the selected path?
/// </summary>
private void SelectedOrgPath_VariableChange(object sender, VariableChangeEventArgs e)
{
    try
    {
        string selectedPath = e.NewValue?.Value as string ?? "";
        string buttonPath = LogicObject.GetVariable("ButtonPath")?.Value.Value as string ?? "";

        bool isSelected = (selectedPath == buttonPath);

        // Update button color based on selection
        UpdateButtonHighlight(isSelected);
    }
    catch (Exception ex)
    {
        Log.Error("Dashboard_OrgNodeNavLogic", $"Error in selection change handler: {ex.Message}");
    }
}

/// <summary>
/// Highlight or unhighlight the button based on selection state
/// </summary>
private void UpdateButtonHighlight(bool isSelected)
{
    if (dashboardButton == null) return;
    Rectangle btnrect = dashboardButton.Owner.Owner as Rectangle;
    //btnrect.FillColor
    Image flimage = dashboardButton.Owner.Get<Image>("Image2");
    Image expandIcon = dashboardButton.Owner.Get<Image>("Image1");

    try
    {
        if (isSelected)
        {
            // ✅ SELECTED STATE - highlight color
            btnrect.FillColor = new Color(255, 52, 73, 94); 
            dashboardButton.TextColor = new Color(255, 255, 255, 255);
            flimage.DynamicColor = new Color(255, 255, 255, 255);
            expandIcon.DynamicColor = new Color(255, 255, 255, 255);
        }
        else
        {
            // ❌ NOT SELECTED - normal color
            btnrect.FillColor = new Color(255, 192, 192, 192); 
            dashboardButton.TextColor = new Color(255, 63, 63, 63);
            flimage.DynamicColor = new Color(255, 106, 106, 106);
            expandIcon.DynamicColor = new Color(255, 63, 63, 63);
        }
    }
    catch (Exception ex)
    {
        Log.Error("Dashboard_OrgNodeNavLogic", $"Error updating highlight: {ex.Message}");
    }
}
}
