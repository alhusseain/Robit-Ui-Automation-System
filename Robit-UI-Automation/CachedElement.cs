using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using System;
using System.Drawing;

class CachedElement
{
    public AutomationElement Element;
    public Rectangle Rect;
    public IntPtr Hwnd;
    public string Name;
    public ControlType ControlType;
}