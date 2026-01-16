# Copilot Instructions

## General Guidelines
- Prefer action buttons (Export To PNG, Import From File, Smart Batch Replace) placed under the Select Assets Folder button in the left pane.
- Prefer TreeView items not to change to gray on selection; keep TreeViewItem foreground controlled by XAML style and avoid programmatically setting them to gray in code-behind.
- Do not modify `MainWindow.xaml.cs` without explicit confirmation; the user has confirmed the restoration of this file to its original version. Restoration is complete, and always ask for permission before making future changes. Ensure that the restoration is exact to the initial version provided and maintain the user's preference of not modifying without confirmation.