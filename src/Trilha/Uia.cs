using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace Trilha;

/// <summary>
/// Acesso mínimo à Automação de Interface do Windows (UI Automation) via COM,
/// só para descobrir o nome e o tipo do elemento clicado ou focado.
/// </summary>
internal sealed class Uia : IDisposable
{
    const int UIA_BoundingRectanglePropertyId = 30001;
    const int UIA_LocalizedControlTypePropertyId = 30004;
    const int UIA_NamePropertyId = 30005;
    const int UIA_IsPasswordPropertyId = 30019;
    const int UIA_ValueValuePropertyId = 30045;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void _CompareElements();
        void _CompareRuntimeIds();
        void _GetRootElement();
        void _ElementFromHandle();
        IUIAutomationElement ElementFromPoint(Native.POINT pt);
        IUIAutomationElement GetFocusedElement();
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        void _SetFocus();
        void _GetRuntimeId();
        void _FindFirst();
        void _FindAll();
        void _FindFirstBuildCache();
        void _FindAllBuildCache();
        void _BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }

    public sealed class ElementInfo
    {
        public string Name = "";
        public string Type = "";
        public bool IsPassword;
        public Rectangle? Bounds;
        public IUIAutomationElement? Raw;

        public string Describe()
        {
            if (Name.Length > 0 && Type.Length > 0) return $"\"{Name}\" ({Type})";
            if (Name.Length > 0) return $"\"{Name}\"";
            if (Type.Length > 0) return Type;
            return "";
        }
    }

    readonly IUIAutomation? _uia;

    public Uia()
    {
        try { _uia = (IUIAutomation)new CUIAutomation(); }
        catch { _uia = null; }
    }

    public ElementInfo? FromPoint(Point p, bool keep = false)
    {
        if (_uia == null) return null;
        try
        {
            var el = _uia.ElementFromPoint(new Native.POINT { X = p.X, Y = p.Y });
            return Read(el, keep);
        }
        catch { return null; }
    }

    public ElementInfo? Focused(bool keep = false)
    {
        if (_uia == null) return null;
        try
        {
            var el = _uia.GetFocusedElement();
            return Read(el, keep);
        }
        catch { return null; }
    }

    /// <summary>Lê o texto atual de um campo (para o passo "Digitou ...").</summary>
    public static string? ReadValue(ElementInfo info)
    {
        if (info.Raw == null || info.IsPassword) return null;
        try
        {
            var v = info.Raw.GetCurrentPropertyValue(UIA_ValueValuePropertyId);
            return v as string;
        }
        catch { return null; }
    }

    public static void Release(ElementInfo? info)
    {
        if (info?.Raw == null) return;
        try { Marshal.ReleaseComObject(info.Raw); } catch { }
        info.Raw = null;
    }

    static ElementInfo? Read(IUIAutomationElement? el, bool keep)
    {
        if (el == null) return null;
        var info = new ElementInfo();
        try { info.Name = Clean(el.GetCurrentPropertyValue(UIA_NamePropertyId) as string); } catch { }
        try { info.Type = Clean(el.GetCurrentPropertyValue(UIA_LocalizedControlTypePropertyId) as string); } catch { }
        try { info.IsPassword = el.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true; } catch { }
        try
        {
            if (el.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is double[] r && r.Length == 4 && r[2] > 0 && r[3] > 0)
                info.Bounds = new Rectangle((int)r[0], (int)r[1], (int)r[2], (int)r[3]);
        }
        catch { }

        if (keep) info.Raw = el;
        else { try { Marshal.ReleaseComObject(el); } catch { } }
        return info;
    }

    static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 80 ? s[..77] + "..." : s;
    }

    public void Dispose()
    {
        if (_uia != null) { try { Marshal.ReleaseComObject(_uia); } catch { } }
    }
}
