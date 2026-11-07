using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class WorkAreaProbe
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (args.Contains("--verify")) {
                var bars = Native.Bars().Where(b => b.Managed && !b.Visible && !b.Primary).ToList();
                if (bars.Count != 2) throw new Exception("Expected two hidden taskbars");
                foreach (Bar bar in bars) {
                    Native.MonitorInfo info;
                    if (!Native.TryMonitor(bar.Device, out info)) throw new Exception("Missing monitor");
                    VerifyWindow(bar.Device, info);
                }
                return 0;
            }
            var originals = new Dictionary<string, Native.MonitorInfo>();
            foreach (Display display in Native.Displays()) {
                Native.MonitorInfo info;
                if (Native.TryMonitor(display.Device, out info)) originals[display.Device] = info;
            }
            string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-original-workareas.json");
            File.WriteAllText(journal, new JavaScriptSerializer().Serialize(originals));
            var touched = new List<string>();
            try {
                foreach (Bar bar in Native.Bars().Where(b => b.Managed && !b.Visible && !b.Primary)) {
                    Native.MonitorInfo info = originals[bar.Device];
                    Native.Rect taskbar;
                    if (!Native.GetWindowRect(new IntPtr(bar.Handle), out taskbar)) continue;
                    if (info.Work.Bottom != taskbar.Top || taskbar.Bottom != info.Monitor.Bottom) continue;
                    Native.Rect expanded = info.Work;
                    expanded.Bottom = info.Monitor.Bottom;
                    touched.Add(bar.Device);
                    if (!Native.SystemParametersInfo(0x2F, 0, ref expanded, 0)) throw new System.ComponentModel.Win32Exception();
                    Console.WriteLine("Expanded " + bar.Device + " from " + (info.Work.Bottom - info.Work.Top) + " to " + (expanded.Bottom - expanded.Top));
                }
                Thread.Sleep(3000);
                foreach (string device in touched) {
                    Native.MonitorInfo current;
                    if (!Native.TryMonitor(device, out current)) throw new Exception("Monitor disappeared");
                    Console.WriteLine("After 3 seconds " + device + ": " + (current.Work.Bottom - current.Work.Top));
                    VerifyWindow(device, current);
                }
                if (touched.Count != 2) throw new Exception("Expected two eligible hidden taskbars");
                return 0;
            } catch (Exception error) { Console.WriteLine(error); return 1; }
            finally {
                foreach (string device in touched) {
                    var rect = originals[device].Work;
                    bool restored = Native.SystemParametersInfo(0x2F, 0, ref rect, 0);
                    Console.WriteLine("Restored " + device + ": " + restored);
                }
                foreach (var item in originals) {
                    Native.MonitorInfo info;
                    if (Native.TryMonitor(item.Key, out info)) Console.WriteLine("Final " + item.Key + ": " + new JavaScriptSerializer().Serialize(info.Work));
                }
            }
        }
        static void VerifyWindow(string device, Native.MonitorInfo current)
        {
            using (var window = new Form()) {
                window.Text = "Taskbar Select - Work area test";
                window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(current.Monitor.Left + 30, current.Monitor.Top + 30);
                window.Size = new Size(500, 300);
                window.Show(); window.WindowState = FormWindowState.Maximized;
                Application.DoEvents(); Thread.Sleep(250); Application.DoEvents();
                Native.Rect frame;
                int result = Native.DwmGetWindowAttribute(window.Handle, 9, out frame, Marshal.SizeOf(typeof(Native.Rect)));
                Console.WriteLine("Maximized frame " + device + ": " + new JavaScriptSerializer().Serialize(frame));
                if (result != 0 || frame.Bottom != current.Monitor.Bottom || frame.Top != current.Work.Top ||
                    frame.Left != current.Work.Left || frame.Right != current.Work.Right)
                    throw new Exception("Maximized window did not fill the work area to the bottom edge");
                window.Close();
            }
        }
    }
}
