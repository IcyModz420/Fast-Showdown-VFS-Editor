using System.Runtime.InteropServices;

namespace FF6PakEditor.UI;

static class Native
{
    public const int WM_SETREDRAW = 0x000B;
    public const int WM_USER = 0x0400;
    public const int EM_GETEVENTMASK = WM_USER + 59;
    public const int EM_GETOLEINTERFACE = WM_USER + 60;
    public const int EM_SETEVENTMASK = WM_USER + 69;
    public const int EM_GETSCROLLPOS = WM_USER + 221;
    public const int EM_SETSCROLLPOS = WM_USER + 222;
    public const int EM_SETTYPOGRAPHYOPTIONS = WM_USER + 202;
    public const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    public const int EM_LINESCROLL = 0x00B6;
    public const int EM_REPLACESEL = 0x00C2;
    public const int EM_SETUNDOLIMIT = WM_USER + 82;

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, out IntPtr lParam);

    public static Point GetScrollPos(IntPtr hwnd)
    {
        var p = Point.Empty;
        SendMessage(hwnd, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
        return p;
    }

    public static void SetScrollPos(IntPtr hwnd, Point p) => SendMessage(hwnd, EM_SETSCROLLPOS, IntPtr.Zero, ref p);
}

// Text Object Model (tom.h). Only the vtable slots up to the methods used here are declared, in order.
[ComImport, Guid("8CC497C0-A1DF-11CE-8098-00AA0047BE5D"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface ITextDocument
{
    [PreserveSig] int GetName(out IntPtr name);
    [PreserveSig] int GetSelection(out IntPtr selection);
    [PreserveSig] int GetStoryCount(out int count);
    [PreserveSig] int GetStoryRanges(out IntPtr stories);
    [PreserveSig] int GetSaved(out int value);
    [PreserveSig] int SetSaved(int value);
    [PreserveSig] int GetDefaultTabStop(out float value);
    [PreserveSig] int SetDefaultTabStop(float value);
    [PreserveSig] int New();
    [PreserveSig] int Open(IntPtr var, int flags, int codePage);
    [PreserveSig] int Save(IntPtr var, int flags, int codePage);
    [PreserveSig] int Freeze(out int count);
    [PreserveSig] int Unfreeze(out int count);
    [PreserveSig] int BeginEditCollection();
    [PreserveSig] int EndEditCollection();
    [PreserveSig] int Undo(int count, out int done);
    [PreserveSig] int Redo(int count, out int done);
    [PreserveSig] int Range(int cpActive, int cpAnchor, out ITextRange range);
}

[ComImport, Guid("8CC497C2-A1DF-11CE-8098-00AA0047BE5D"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface ITextRange
{
    [PreserveSig] int GetText(out IntPtr text);
    [PreserveSig] int SetText(IntPtr text);
    [PreserveSig] int GetChar(out int ch);
    [PreserveSig] int SetChar(int ch);
    [PreserveSig] int GetDuplicate(out IntPtr range);
    [PreserveSig] int GetFormattedText(out IntPtr range);
    [PreserveSig] int SetFormattedText(IntPtr range);
    [PreserveSig] int GetStart(out int cp);
    [PreserveSig] int SetStart(int cp);
    [PreserveSig] int GetEnd(out int cp);
    [PreserveSig] int SetEnd(int cp);
    [PreserveSig] int GetFont(out ITextFont font);
    [PreserveSig] int SetFont(IntPtr font);
    [PreserveSig] int GetPara(out IntPtr para);
    [PreserveSig] int SetPara(IntPtr para);
    [PreserveSig] int GetStoryLength(out int count);
    [PreserveSig] int GetStoryType(out int value);
    [PreserveSig] int Collapse(int start);
    [PreserveSig] int Expand(int unit, out int delta);
    [PreserveSig] int GetIndex(int unit, out int index);
    [PreserveSig] int SetIndex(int unit, int index, int extend);
    [PreserveSig] int SetRange(int cpAnchor, int cpActive);
}

[ComImport, Guid("8CC497C3-A1DF-11CE-8098-00AA0047BE5D"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface ITextFont
{
    [PreserveSig] int GetDuplicate(out IntPtr font);
    [PreserveSig] int SetDuplicate(IntPtr font);
    [PreserveSig] int CanChange(out int value);
    [PreserveSig] int IsEqual(IntPtr font, out int value);
    [PreserveSig] int Reset(int value);
    [PreserveSig] int GetStyle(out int value);
    [PreserveSig] int SetStyle(int value);
    [PreserveSig] int GetAllCaps(out int value);
    [PreserveSig] int SetAllCaps(int value);
    [PreserveSig] int GetAnimation(out int value);
    [PreserveSig] int SetAnimation(int value);
    [PreserveSig] int GetBackColor(out int value);
    [PreserveSig] int SetBackColor(int value);
    [PreserveSig] int GetBold(out int value);
    [PreserveSig] int SetBold(int value);
    [PreserveSig] int GetEmboss(out int value);
    [PreserveSig] int SetEmboss(int value);
    [PreserveSig] int GetForeColor(out int value);
    [PreserveSig] int SetForeColor(int value);
}
