using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Trilha;

/// <summary>
/// Observa cliques e teclas no Windows inteiro e transforma cada ação num passo com print.
/// Os ganchos (hooks) rodam numa thread própria que só tira o print e enfileira;
/// o trabalho mais lento (ler o nome do botão, comprimir a imagem) roda em outra thread.
/// </summary>
public sealed class Recorder : IDisposable
{
    public event Action<Step>? StepCaptured;
    public event Action<Step>? StepUpdated;

    abstract record Ev(DateTime Time);
    sealed record ClickEv(DateTime Time, Point Pt, MouseButtons Button, Bitmap Shot, Rectangle Monitor, string Window, uint Pid, bool FlushTyping) : Ev(Time);
    sealed record KeyEv(DateTime Time, string Combo, bool IsEnterOrTab, Bitmap Shot, string Window, uint Pid, bool FlushTyping) : Ev(Time);
    sealed record TypingEv(DateTime Time, string Window, uint Pid) : Ev(Time);
    sealed record StopEv(DateTime Time) : Ev(Time);

    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly BlockingCollection<Ev> _queue = new();
    Thread? _hookThread, _worker;
    uint _hookThreadId;
    IntPtr _mouseHook, _keyHook;
    Native.LowLevelProc? _mouseProc, _keyProc; // mantidos vivos contra o coletor de lixo

    volatile bool _paused;
    bool _typingPending; // só usado na thread dos ganchos

    public bool RecordTyping { get; set; } = true;

    /// <summary>Atalhos do próprio Nider Etapas, que não devem virar passos.</summary>
    public HashSet<string> IgnoredCombos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Paused { get => _paused; set => _paused = value; }

    public void Start()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Trilha worker" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();

        var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "Trilha hooks" };
        _hookThread.Start();
        ready.Wait(3000);
    }

    /// <summary>Para de gravar e espera os últimos passos ficarem prontos.</summary>
    public void Stop()
    {
        if (_hookThread != null)
        {
            Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(3000);
            _hookThread = null;
        }
        if (_worker != null)
        {
            _queue.Add(new StopEv(DateTime.Now));
            _queue.CompleteAdding();
            _worker.Join(15000);
            _worker = null;
        }
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- ganchos

    void HookLoop(ManualResetEventSlim ready)
    {
        _hookThreadId = Native.GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        var mod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
        ready.Set();

        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var msg = (int)wParam;
            if (nCode >= 0 && !_paused && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN))
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var hwnd = Native.WindowFromPoint(data.pt);
                var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
                var pid = Native.ProcessOf(root);
                if (pid != _ownPid)
                {
                    var pt = new Point(data.pt.X, data.pt.Y);
                    var monitor = Screen.FromPoint(pt).Bounds;
                    var shot = Imaging.Capture(monitor);
                    var button = msg == Native.WM_LBUTTONDOWN ? MouseButtons.Left
                               : msg == Native.WM_RBUTTONDOWN ? MouseButtons.Right : MouseButtons.Middle;
                    var flush = _typingPending;
                    _typingPending = false;
                    _queue.Add(new ClickEv(DateTime.Now, pt, button, shot, monitor, Native.WindowTitle(root), pid, flush));
                }
            }
        }
        catch (Exception ex) { Log.Error("MouseHook", ex); /* nunca travar o mouse do usuário */ }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var msg = (int)wParam;
            if (nCode >= 0 && !_paused && (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                HandleKey((Keys)data.vkCode);
            }
        }
        catch { }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    void HandleKey(Keys key)
    {
        if (IsModifier(key)) return;
        var fg = Native.GetForegroundWindow();
        var pid = Native.ProcessOf(fg);
        if (pid == _ownPid) return;

        bool ctrl = Native.IsKeyDown(Keys.ControlKey), alt = Native.IsKeyDown(Keys.Menu);
        bool shift = Native.IsKeyDown(Keys.ShiftKey);
        bool win = Native.IsKeyDown(Keys.LWin) || Native.IsKeyDown(Keys.RWin);
        bool altGr = ctrl && alt; // AltGr no teclado ABNT gera caracteres (/, ?, °)

        bool isFn = key >= Keys.F1 && key <= Keys.F24;
        bool isEnterTab = key == Keys.Enter || key == Keys.Tab;
        bool special = isEnterTab || key == Keys.Escape || isFn || ((ctrl || alt || win) && !altGr);

        if (!special)
        {
            // Setas, Home/End etc. sozinhas não viram passo.
            if (IsNavigation(key) || !RecordTyping) return;
            _typingPending = true;
            _queue.Add(new TypingEv(DateTime.Now, Native.WindowTitle(fg), pid));
            return;
        }

        var combo = new List<string>();
        if (ctrl && !altGr) combo.Add("Ctrl");
        if (alt && !altGr) combo.Add("Alt");
        if (shift && (ctrl || alt || win || isFn || isEnterTab)) combo.Add("Shift");
        if (win) combo.Add("Win");
        combo.Add(KeyName(key));
        var comboText = string.Join("+", combo);
        if (IgnoredCombos.Contains(comboText)) return;

        var monitor = fg != IntPtr.Zero ? Screen.FromHandle(fg).Bounds : Screen.PrimaryScreen!.Bounds;
        var shot = Imaging.Capture(monitor);
        var flush = _typingPending && RecordTyping;
        _typingPending = false;
        _queue.Add(new KeyEv(DateTime.Now, comboText, isEnterTab && combo.Count == 1, shot, Native.WindowTitle(fg), pid, flush));
    }

    static bool IsNavigation(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
        or Keys.PageUp or Keys.PageDown or Keys.Insert or Keys.Apps or Keys.Scroll or Keys.Pause
        or Keys.VolumeUp or Keys.VolumeDown or Keys.VolumeMute or Keys.MediaNextTrack or Keys.MediaPreviousTrack or Keys.MediaPlayPause;

    static bool IsModifier(Keys k) => k is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.ControlKey
        or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin
        or Keys.CapsLock or Keys.NumLock;

    static string KeyName(Keys k) => k switch
    {
        Keys.Enter => "Enter",
        Keys.Escape => "Esc",
        Keys.Tab => "Tab",
        Keys.Back => "Backspace",
        Keys.Space => "Espaço",
        Keys.Delete => "Delete",
        Keys.Insert => "Insert",
        Keys.Home => "Home",
        Keys.End => "End",
        Keys.PageUp => "Page Up",
        Keys.PageDown => "Page Down",
        Keys.Left => "Seta para a esquerda",
        Keys.Right => "Seta para a direita",
        Keys.Up => "Seta para cima",
        Keys.Down => "Seta para baixo",
        Keys.PrintScreen => "Print Screen",
        >= Keys.D0 and <= Keys.D9 => ((int)(k - Keys.D0)).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => ((int)(k - Keys.NumPad0)).ToString(),
        >= Keys.A and <= Keys.Z => k.ToString(),
        >= Keys.F1 and <= Keys.F24 => k.ToString(),
        _ => OemName(k),
    };

    static string OemName(Keys k)
    {
        // Converte teclas como OemPeriod no caractere impresso nelas.
        var ch = MapVirtualKey((uint)k, 2 /*MAPVK_VK_TO_CHAR*/) & 0xFFFF;
        return ch > 32 ? ((char)ch).ToString() : k.ToString();
    }

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // ---------------------------------------------------------------- processamento

    Step? _lastClick;
    Uia.ElementInfo? _typingField;
    string _typingWindow = "";
    string? _typingValue;
    uint _typingPid;

    void WorkerLoop()
    {
        using var uia = new Uia();
        foreach (var ev in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (ev)
                {
                    case ClickEv c: OnClick(uia, c); break;
                    case KeyEv k: OnKey(k); break;
                    case TypingEv t: OnTyping(uia, t); break;
                    case StopEv:
                        if (_typingField != null || _typingWindow.Length > 0)
                        {
                            using var shot = Imaging.Capture(Screen.FromPoint(Cursor.Position).Bounds);
                            FlushTyping(shot, null);
                        }
                        break;
                }
            }
            catch (Exception ex) { Log.Error("Recorder", ex); /* um passo com erro não pode derrubar a gravação */ }
        }
    }

    void OnClick(Uia uia, ClickEv c)
    {
        using var shot = c.Shot;
        if (c.FlushTyping) FlushTyping(shot, null);

        // Dois cliques rápidos no mesmo lugar viram um "clique duplo" no passo anterior.
        if (c.Button == MouseButtons.Left && _lastClick?.ClickPoint is Point lp &&
            (c.Time - _lastClick.ClickTime).TotalMilliseconds <= Native.GetDoubleClickTime() &&
            Math.Abs(lp.X - c.Pt.X) <= 6 && Math.Abs(lp.Y - c.Pt.Y) <= 6 &&
            _lastClick.Description.StartsWith("Clique em", StringComparison.Ordinal))
        {
            _lastClick.Description = "Clique duplo em" + _lastClick.Description["Clique em".Length..];
            _lastClick.ClickPoint = null;
            StepUpdated?.Invoke(_lastClick);
            return;
        }

        var info = uia.FromPoint(c.Pt);
        var what = info?.Describe() ?? "";
        var verb = c.Button switch
        {
            MouseButtons.Right => "Clique com o botão direito",
            MouseButtons.Middle => "Clique com o botão do meio",
            _ => "Clique",
        };
        var text = what.Length > 0 ? $"{verb} em {what}" : verb;
        text += Where(c.Window, c.Pid);

        var step = Step.FromBitmap(shot);
        step.Time = c.Time;
        step.Description = text;
        Rectangle? el = null;
        if (info?.Bounds is Rectangle b)
        {
            var local = b;
            local.Offset(-c.Monitor.X, -c.Monitor.Y);
            local.Intersect(new Rectangle(Point.Empty, c.Monitor.Size));
            // Não contorna áreas enormes (a janela inteira), só o botão/campo.
            if (local.Width * local.Height < c.Monitor.Width * c.Monitor.Height / 3) el = local;
        }
        step.Marks.Add(new ClickMark { Center = new Point(c.Pt.X - c.Monitor.X, c.Pt.Y - c.Monitor.Y), Element = el });
        // Clique na própria barra de tarefas (ex.: botão Iniciar) mantém a barra no print.
        if (Screen.FromPoint(c.Pt).WorkingArea.Contains(c.Pt)) step.TrimTaskbar(c.Monitor);
        if (c.Button == MouseButtons.Left) { step.ClickPoint = c.Pt; step.ClickTime = c.Time; }
        _lastClick = step;
        StepCaptured?.Invoke(step);
    }

    void OnKey(KeyEv k)
    {
        using var shot = k.Shot;
        if (k.FlushTyping && k.IsEnterOrTab)
        {
            FlushTyping(shot, k.Combo);
            return;
        }
        if (k.FlushTyping) FlushTyping(shot, null);

        var step = Step.FromBitmap(shot);
        step.Time = k.Time;
        step.Description = $"Pressionou {k.Combo}" + Where(k.Window, k.Pid);
        step.TrimTaskbar(Step.MonitorFor(shot));
        _lastClick = null;
        StepCaptured?.Invoke(step);
    }

    void OnTyping(Uia uia, TypingEv t)
    {
        if (_typingField == null && _typingWindow.Length == 0)
        {
            _typingField = uia.Focused(keep: true);
            _typingWindow = t.Window.Length > 0 ? t.Window : " ";
            _typingPid = t.Pid;
        }
        // Guarda o texto do campo a cada tecla: depois do Enter ele pode já ter sumido.
        if (_typingField != null && !_typingField.IsPassword)
        {
            var v = Uia.ReadValue(_typingField);
            if (!string.IsNullOrWhiteSpace(v)) _typingValue = v;
        }
    }

    void FlushTyping(Bitmap shot, string? thenKey)
    {
        if (_typingField == null && _typingWindow.Length == 0) return;
        var field = _typingField;
        string text;
        var where = field?.Describe() ?? "";
        var value = _typingValue ?? (field != null ? Uia.ReadValue(field) : null);
        if (field?.IsPassword == true) text = "Digitou a senha";
        else if (!string.IsNullOrWhiteSpace(value))
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length > 60) value = value[..57] + "...";
            text = $"Digitou \"{value}\"";
        }
        else text = "Digitou texto";
        if (where.Length > 0) text += $" em {where}";
        if (thenKey != null) text += $" e pressionou {thenKey}";
        text += Where(_typingWindow.Trim(), _typingPid);

        Uia.Release(field);
        _typingField = null;
        _typingWindow = "";
        _typingValue = null;
        _typingPid = 0;

        var step = Step.FromBitmap(shot);
        step.Description = text;
        step.TrimTaskbar(Step.MonitorFor(shot));
        _lastClick = null;
        StepCaptured?.Invoke(step);
    }

    static string Where(string window, uint pid)
    {
        if (window.Length > 0)
        {
            if (window.Length > 70) window = window[..67] + "...";
            return $" na janela \"{window}\"";
        }
        var name = Native.ProcessName(pid);
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return " na área de trabalho / barra de tarefas";
        return name.Length > 0 ? $" no programa \"{name}\"" : "";
    }
}
