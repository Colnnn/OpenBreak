# Using OpenBreak

OpenBreak runs on Windows 11 x64. The first preview has yet to be published. When downloads are available on [GitHub Releases](https://github.com/Colnnn/OpenBreak/releases), use the installer or extract the whole standalone ZIP and open `OpenBreak.exe` from the extracted folder. You can also [build from source](building.md).

Current preview builds are unsigned. Windows may show a SmartScreen warning or block the app, depending on your security settings. Before opening a download, compare its SHA-256 hash with the value in `SHA256SUMS.txt` from the same release. [Microsoft SmartScreen guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation).

## Build your task board

Choose **Add task** on the **Tasks** page, or press **Ctrl+Alt+K** from another app to open Quick capture. A title is enough to save. Add a next step if it will help you restart later. Use the task card’s menu to start, complete, edit, or archive a task. In **Archive**, use **Restore** to put a task back on the board. The trash button permanently deletes the archived task as soon as you select it.

If the capture shortcut is unavailable, choose **Capture task** from the OpenBreak tray menu. To choose a new shortcut, open **Capture**, enter a combination such as **Ctrl+Shift+K**, and choose **Apply shortcut**.

## Set up breaks

Scheduled reminders start off. On **Focus**, choose **Preview a break** to try the display. In **Breaks**, set work and break lengths, optional long breaks and working hours, and the snooze duration. Choose **Save settings**, then select the play button labeled **Start focus** on **Focus**. While the timer is running, use the pause button labeled **Pause work** and the play button labeled **Resume work**. You can also enable **Scheduled reminders** in **Breaks** and save the setting there.

The default break display shows your saved tasks on a read-only board. To show less, choose **One task** or **Timer only** under **Break display**. Use **Pause for presentation** from the tray before sharing your screen. When a break approaches, the warning offers **Snooze** and **Skip break**. Optional **Finish typing before a break** holds the final warning second until there have been three seconds without keyboard or mouse input. Choose **End break** to leave a break early.

## Keep the timer nearby

In **Appearance**, you can enable **Show countdown in the system tray** or **Show a floating desktop timer**, then choose **Save appearance**. Look in the Windows notification area overflow if the OpenBreak icon is hidden. Closing the main window keeps the app available in the tray. Choose **Quit OpenBreak** there to exit.

## Manage local data

OpenBreak keeps tasks and settings in `%LOCALAPPDATA%\Afterbreak\tasks.db`. The folder name comes from the app’s earlier name. The installer, extracted ZIP, and source build use the same data for your Windows account. No account is required.

To back up your data, choose **Quit OpenBreak** and copy the entire `%LOCALAPPDATA%\Afterbreak` folder to a private backup location. To restore it, quit OpenBreak, keep a copy of the current folder, and replace it with your backup before reopening the app. Back up before changing preview versions. Uninstalling OpenBreak leaves the data folder in place.

## Troubleshooting

- **OpenBreak appears to be missing.** Look in the notification area overflow and choose **Open OpenBreak**.
- **The timer isn’t running.** Check **Focus** for the current status. If you used **Pause for presentation**, choose **Resume after presentation** from the tray.
- **Setup or uninstall asks you to close the app.** Use **Quit OpenBreak** from the tray, then retry.

For other problems, [open an issue](https://github.com/Colnnn/OpenBreak/issues) with the app version, Windows version, and steps to reproduce using sample tasks. Keep your database and personal task contents private.
