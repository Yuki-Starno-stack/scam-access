// Scam Access installer ("Scam Access.exe"). Each run first asks GitHub whether a newer version is out; if so it
// downloads that one and runs it instead, so an old copy of this file always installs the newest mod.
// Then it finds the game through Steam and unpacks the mod loader (BepInEx) and the mod into it.
// All prompts are standard message boxes, which screen readers read.
// Compiled by build-installers.ps1. BuildInfo.Version comes from Updater.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Installer
{
    const string Game = "Scam With Your Friends";
    const string GameExe = Game + ".exe";
    const string Releases = "https://github.com/Yuki-Starno-stack/scam-access/releases/latest/download/";
    const string Title = "Scam Access Setup";
    const string OnlineName = "Scam.Access.exe";

    class ShortWebClient : WebClient
    {
        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest r = base.GetWebRequest(address);
            r.Timeout = 20000;
            return r;
        }
    }

    // Returns true when a newer copy was downloaded and started; this copy should then just exit.
    static bool RunNewerVersion()
    {
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2, which GitHub requires
            using (var web = new ShortWebClient())
            {
                Version latest;
                if (!Version.TryParse(web.DownloadString(Releases + "version.txt").Trim(), out latest)) return false;
                if (latest <= new Version(BuildInfo.Version)) return false;
                string path = Path.Combine(Path.GetTempPath(), "Scam Access " + latest + ".exe");
                web.DownloadFile(Releases + OnlineName, path);
                if (new FileInfo(path).Length < 100000) return false;
                Process.Start(path, "/fromupdate");
                return true;
            }
        }
        catch { return false; } // offline or GitHub unreachable: install the version inside this file
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool fresh = args.Length > 0 && args[0] == "/fromupdate";
        if (args.Length == 0 && RunNewerVersion()) return 0;
        if (fresh) args = new string[0];

        // Testing: "/silent <folder>" installs into that folder with no prompts.
        if (args.Length == 2 && args[0] == "/silent")
        {
            try { Install(args[1]); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }

        // Started by version 0.2.0 of the mod after it downloaded an update: wait for the game to close, then install.
        if (args.Length == 2 && args[0] == "/update")
        {
            try
            {
                while (Process.GetProcessesByName(Game).Length > 0) System.Threading.Thread.Sleep(1000);
                for (int attempt = 0; ; attempt++)
                {
                    System.Threading.Thread.Sleep(2000); // files stay locked for a moment after the game exits
                    try { Install(args[1]); break; }
                    catch (IOException) { if (attempt >= 5) throw; }
                }
                Info("Scam Access is updated. Press Enter, then start the game again from Steam.");
                return 0;
            }
            catch (Exception e)
            {
                Error("The Scam Access update failed.\n\n" + e.Message);
                return 1;
            }
        }

        try
        {
            string dir = FindGame();
            if (dir == null)
            {
                Info("The game was not found automatically. In the next window, choose the folder where " + Game + " is installed. It is the folder that contains " + GameExe + ".");
                dir = AskForFolder();
                if (dir == null) return 1;
            }

            // Running the installer over an older copy is how people update.
            bool update = Directory.Exists(Path.Combine(dir, @"BepInEx\plugins\ScamAccess"));
            string intro = update
                ? "Scam Access is already installed in this game. This updates it to version " + BuildInfo.Version + ", the newest. Your settings are kept.\n\nGame folder:\n" + dir + "\n\nUpdate now?"
                : "This installs Scam Access " + BuildInfo.Version + ", the screen reader mod for " + Game + ".\n\nGame folder:\n" + dir + "\n\nInstall now?";
            if (MessageBox.Show(intro, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;

            while (Process.GetProcessesByName(Game).Length > 0)
            {
                if (MessageBox.Show("The game is running. Close it, then choose OK to continue.", Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return 1;
            }

            Install(dir);

            string done = (update ? "Scam Access is updated." : "Scam Access is installed.") + " Start the game from Steam with NVDA running.\n\n"
                + "Main keys:\n"
                + "Arrow keys move through menus, Enter selects.\n"
                + "F9 reads the screen. F10 repeats the last message. F5 says your status.\n"
                + "V mutes or unmutes your microphone in the game.\n"
                + "Control+C copies the lobby invite code for your friends.\n"
                + "Control+K copies the number a caller just gave you.\n"
                + "In the boss fight, semicolon punches Mr Curry and Slash faces him.\n"
                + "Control+Q twice closes the game.\n"
                + "To update later, run Scam Access.exe again. It always fetches the newest version.\n"
                + "F1 at the in-game computer lists its keys.\n"
                + "F11 sets the game language back to English.\n";
            Info(done);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Error("Windows did not allow writing to the game folder. Right-click this installer, choose Run as administrator, and try again.");
            return 1;
        }
        catch (Exception e)
        {
            Error("The installation failed.\n\n" + e.Message);
            return 1;
        }
    }

    // A topmost owner so the message comes to the front even when nothing of ours had focus.
    static void Show(string text, MessageBoxIcon icon)
    {
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false })
            MessageBox.Show(owner, text, Title, MessageBoxButtons.OK, icon);
    }
    static void Info(string text) { Show(text, MessageBoxIcon.Information); }
    static void Error(string text) { Show(text, MessageBoxIcon.Error); }

    static bool IsGameFolder(string dir)
    {
        return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, GameExe));
    }

    // Steam keeps its folder in the registry and its extra libraries in libraryfolders.vdf.
    static string FindGame()
    {
        var steamDirs = new List<string>();
        string[] keys =
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam|SteamPath",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam|InstallPath",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam|InstallPath",
        };
        foreach (string key in keys)
        {
            string[] parts = key.Split('|');
            try
            {
                string value = Registry.GetValue(parts[0], parts[1], null) as string;
                if (!string.IsNullOrEmpty(value)) steamDirs.Add(value.Replace('/', '\\'));
            }
            catch { }
        }

        var libraries = new List<string>(steamDirs);
        foreach (string steam in steamDirs)
        {
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
        }

        foreach (string library in libraries)
        {
            string dir = Path.Combine(library, @"steamapps\common\" + Game);
            if (IsGameFolder(dir)) return dir;
        }
        return null;
    }

    static string AskForFolder()
    {
        while (true)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the " + Game + " folder";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() != DialogResult.OK) return null;
                if (IsGameFolder(dialog.SelectedPath)) return dialog.SelectedPath;
            }
            if (MessageBox.Show("That folder does not contain " + GameExe + ". Choose another folder?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return null;
        }
    }

    static void Install(string dir)
    {
        string root = Path.GetFullPath(dir);
        using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) continue;
                string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, true);
            }
        }
    }
}
