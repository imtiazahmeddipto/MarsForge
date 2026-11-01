using System;
using System.IO;
using System.Windows.Forms;

namespace MarsForge.Launcher
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // build step (used by build.bat): MarsForge.exe --pack <app folder> <output exe>
            if (args.Length == 3 && args[0] == "--pack")
            {
                try
                {
                    Package.Pack(Application.ExecutablePath, args[1], args[2]);
                    return 0;
                }
                catch (Exception e)
                {
                    try { File.AppendAllText("build.log", Environment.NewLine + "Packing failed: " + e + Environment.NewLine); } catch { }
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Payload payload;
            try { payload = Package.Find(Application.ExecutablePath); }
            catch (Exception e) { payload = null; MessageBox.Show(e.Message, "MarsForge"); }
            if (payload == null)
            {
                MessageBox.Show("This MarsForge.exe is incomplete. Please build it again with build.bat.", "MarsForge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Application.Run(new LoadingForm(payload, args));
            return 0;
        }
    }
}
