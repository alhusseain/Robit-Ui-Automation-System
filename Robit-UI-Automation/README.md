# Robit UI Automation System

A high-performance UI automation and element tracking client for the Robit project, built using C#, FlaUI (UIA3), and WebSockets.

---

## 🛠 Features
*   **Real-time Element Tracking**: Traverses active windows and tracks user interface elements closest to the mouse pointer.
*   **Overlay Visualization**: Draws red highlighted boxes with numeric indices over target elements.
*   **Diagnostic Tools**: A built-in diagnostic mode to inspect and log the UI Automation tree of any window.
*   **Touch Keyboard Key Support**: Traverses and highlights modern UWP Windows Touch Keyboard keys.

---

## 🔍 Diagnostic Mode

The project includes a PowerShell script `diagnose.ps1` to quickly build the application and query any running window's UIA structure.

### List all visible windows:
```powershell
.\diagnose.ps1 -List
```

### Inspect a specific window by title substring:
```powershell
.\diagnose.ps1 -Name "File Explorer"
```

### Inspect a specific window by HWND handle:
```powershell
.\diagnose.ps1 -Id 0x000204AE
```

---

## ⌨️ Touch Keyboard (TabTip) Automation Instructions

Windows places the touch keyboard (`TabTip.exe` / `TextInputHost.exe` / `"Windows Input Experience"`) in a highly protected system integrity layer. To automate or inspect the keyboard's letter/control keys, follow these instructions:

### 1. Privilege Requirement (Run as Administrator)
Because of Windows User Interface Privilege Isolation (UIPI), **standard-user programs cannot read the touch keyboard's keys**. UIA will simply return `0` descendants.
*   **Requirement**: You **must** run your diagnostics terminal, your automation scripts, or your main production application as **Administrator** to fetch and interact with the keyboard keys.

### 2. High Z-Order Overlay Highlight (`UIAccess` Deployment)
By default, Windows places the touch keyboard in a system-reserved Z-order band (`ZBID_UIACCESS`), which sits above standard `TopMost` windows. Our overlay highlight squares will be drawn **behind/below** the keyboard.

To force the overlay window to render **on top** of the touch keyboard:

1.  Open **PowerShell as Administrator** (Right-click -> *Run as Administrator*).
2.  Navigate to the project directory and run the UIAccess deployment script:
    ```powershell
    cd "d:\Projects\Robit Ui Automation System\Robit-UI-Automation"
    Set-ExecutionPolicy Bypass -Scope Process -Force
    .\deploy_uiaccess.ps1
    ```
    *This script builds the app in Release mode, generates a trusted local code-signing certificate, signs the executable, and deploys it to a secure path.*
3.  Launch the application from its secure deployment directory:
    ```powershell
    & "C:\Program Files\RobitUiAutomation\Robit-UI-Automation.exe"
    ```

Windows will now permit the application to run in the `UIACCESS` band, allowing the red highlight boxes to render cleanly over the keyboard.
