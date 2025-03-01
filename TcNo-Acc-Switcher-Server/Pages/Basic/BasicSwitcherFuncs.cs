﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TcNo_Acc_Switcher_Globals;
using TcNo_Acc_Switcher_Server.Data;
using TcNo_Acc_Switcher_Server.Pages.General;
using BasicSettings = TcNo_Acc_Switcher_Server.Data.Settings.Basic;

namespace TcNo_Acc_Switcher_Server.Pages.Basic
{
    public class BasicSwitcherFuncs
    {
        private static readonly Lang Lang = Lang.Instance;

        /// <summary>
        /// Main function for Basic Account Switcher. Run on load.
        /// Collects accounts from cache folder
        /// Prepares HTML Elements string for insertion into the account switcher GUI.
        /// </summary>
        /// <returns>Whether account loading is successful, or a path reset is needed (invalid dir saved)</returns>
        public static void LoadProfiles()
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.LoadProfiles] Loading Basic profiles for: " + CurrentPlatform.FullName);
            _ = GenericFunctions.GenericLoadAccounts(CurrentPlatform.FullName, true);
        }

        /// <summary>
        /// Used in JS. Gets whether forget account is enabled (Whether to NOT show prompt, or show it).
        /// </summary>
        /// <returns></returns>
        [JSInvokable]
        public static Task<bool> GetBasicForgetAcc() => Task.FromResult(BasicSettings.ForgetAccountEnabled);

        #region Account IDs

        public static Dictionary<string, string> AccountIds;
        public static void LoadAccountIds() => AccountIds = GeneralFuncs.ReadDict(CurrentPlatform.IdsJsonPath);
        //public static void LoadAccountIds()
        //{
        //    var p = Path.GetFullPath(CurrentPlatform.IdsJsonPath);
        //    AccountIds = GeneralFuncs.ReadDict(p);
        //    return;
        //}
        private static void SaveAccountIds() =>
            File.WriteAllText(CurrentPlatform.IdsJsonPath, JsonConvert.SerializeObject(AccountIds));
        public static string GetNameFromId(string accId) => AccountIds.ContainsKey(accId) ? AccountIds[accId] : accId;
        #endregion

        /// <summary>
        /// Restart Basic with a new account selected. Leave args empty to log into a new account.
        /// </summary>
        /// <param name="accId">(Optional) User's unique account ID</param>
        /// <param name="args">Starting arguments</param>
        [SupportedOSPlatform("windows")]
        public static void SwapBasicAccounts(string accId = "", string args = "")
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.SwapBasicAccounts] Swapping to: hidden.");
            // Handle args:
            if (CurrentPlatform.ExeExtraArgs != "")
            {
                args = CurrentPlatform.ExeExtraArgs + (args == "" ? "" : " " + args);
            }

            LoadAccountIds();
            var accName = GetNameFromId(accId);

            if (!KillGameProcesses())
                return;

            // Add currently logged in account if there is a way of checking unique ID.
            // If saved, and has unique key: Update
            if (CurrentPlatform.UniqueIdFile is not null)
            {
                string uniqueId;
                if (CurrentPlatform.UniqueIdMethod is "REGKEY" && !string.IsNullOrEmpty(CurrentPlatform.UniqueIdFile))
                {
                    _ = ReadRegistryKeyWithErrors(CurrentPlatform.UniqueIdFile, out var t);
                    if (t is string s) uniqueId = s;
                    else if (t is byte[]) uniqueId = Globals.GetSha256HashString(t);
                    else
                    {
                        Globals.WriteToLog("Unexpected registry type encountered! Report to TechNobo.");
                        return;
                    }
                }
                else
                    uniqueId = GetUniqueId();

                // UniqueId Found >> Save!
                if (File.Exists(CurrentPlatform.IdsJsonPath))
                {
                    if (!string.IsNullOrEmpty(uniqueId) && AccountIds.ContainsKey(uniqueId))
                    {
                        if (accId == uniqueId)
                        {
                            _ = GeneralInvocableFuncs.ShowToast("info", Lang["Toast_AlreadyLoggedIn"], renderTo: "toastarea");
                            if (BasicSettings.AutoStart)
                            {
                                if (Globals.StartProgram(BasicSettings.Exe(), BasicSettings.Admin, args, CurrentPlatform.StartingMethod))
                                    _ = GeneralInvocableFuncs.ShowToast("info", Lang["Status_StartingPlatform", new { platform = CurrentPlatform.SafeName }], renderTo: "toastarea");
                                else
                                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_StartingPlatformFailed", new { platform = CurrentPlatform.SafeName }], renderTo: "toastarea");
                            }
                            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Done"]);

                            return;
                        }
                        BasicAddCurrent(AccountIds[uniqueId]);
                    }
                }
            }

            // Clear current login
            ClearCurrentLoginBasic();

            // Copy saved files in
            if (accName != "")
            {
                if (!BasicCopyInAccount(accId)) return;
                Globals.AddTrayUser(CurrentPlatform.SafeName, $"+{CurrentPlatform.PrimaryId}:" + accId, accName, BasicSettings.TrayAccNumber); // Add to Tray list, using first Identifier
            }

            if (BasicSettings.AutoStart)
                BasicSettings.RunPlatform(BasicSettings.Exe(), BasicSettings.Admin, args, CurrentPlatform.FullName, CurrentPlatform.StartingMethod);

            NativeFuncs.RefreshTrayArea();
            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Done"]);
        }

        public static void StartPlatform(){}


        [SupportedOSPlatform("windows")]
        private static bool ClearCurrentLoginBasic()
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.ClearCurrentLoginBasic]");

            // Foreach file/folder/reg in Platform.PathListToClear
            if (CurrentPlatform.PathListToClear.Any(accFile => !DeleteFileOrFolder(accFile)))
                return false;

            if (CurrentPlatform.UniqueIdMethod != "CREATE_ID_FILE") return true;

            // Unique ID file --> This needs to be deleted for a new instance
            var uniqueIdFile = CurrentPlatform.GetUniqueFilePath();
            Globals.DeleteFile(uniqueIdFile);

            return true;
        }

        public static void ClearCache()
        {

            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.ClearCache]");
            var totalFiles = 0;
            var totalSize = Globals.FileLengthToString(CurrentPlatform.CachePaths.Sum(x => SizeOfFile(x, ref totalFiles)));
            _ = GeneralInvocableFuncs.ShowToast("info", Lang["Platform_ClearCacheTotal", new { totalFileCount = totalFiles, totalSizeMB = totalSize }], Lang["Working"], "toastarea");

            // Foreach file/folder/reg in Platform.PathListToClear
            foreach (var f in CurrentPlatform.CachePaths.Where(f => !DeleteFileOrFolder(f)))
            {
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Platform_CouldNotDeleteLog", new { logPath = Globals.GetLogPath() }], Lang["Working"], "toastarea");
                Globals.WriteToLog("Could not delete: " + f);
            }

            _ = GeneralInvocableFuncs.ShowToast("success", Lang["DeletedFiles"], Lang["Working"], "toastarea");
        }

        private static long SizeOfFile(string accFile, ref int numFiles)
        {
            // The "file" is a registry key
            if (accFile.StartsWith("REG:"))
                return 0;

            long totalSize = 0;
            numFiles = 0;

            // Handle wildcards
            DirectoryInfo di;
            if (accFile.Contains('*'))
            {
                var folder = ExpandEnvironmentVariables(Path.GetDirectoryName(accFile) ?? "");
                var file = Path.GetFileName(accFile);
                di = new DirectoryInfo(folder);

                var so = SearchOption.TopDirectoryOnly;
                var searchPattern = file;
                // "...\\*" is recursive
                if (file == "*")
                {
                    searchPattern = "*";
                    so = SearchOption.AllDirectories;
                }

                // while "...\\*.log" or "...\\file_*" are not.
                foreach (var fi in di.EnumerateFiles(searchPattern, so))
                {
                    totalSize += fi.Length;
                    numFiles++;
                }

                return totalSize;
            }

            var fullPath = ExpandEnvironmentVariables(accFile);
            // Is folder? Recursive get file size
            if (Directory.Exists(fullPath))
            {
                di = new DirectoryInfo(fullPath);
                foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    totalSize += fi.Length;
                    numFiles++;
                }

                return totalSize;
            }

            // Is file? Get file size
            if (!File.Exists(fullPath)) return 0;
            numFiles++;
            return new FileInfo(fullPath).Length;
        }

        private static bool DeleteFileOrFolder(string accFile)
        {
            // The "file" is a registry key
            if (OperatingSystem.IsWindows() && accFile.StartsWith("REG:"))
            {
                if (Globals.SetRegistryKey(accFile[4..])) return true;
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailWrite"], Lang["Error"], "toastarea");
                return false;
            }

            // Handle wildcards
            if (accFile.Contains('*'))
            {
                var folder = ExpandEnvironmentVariables(Path.GetDirectoryName(accFile) ?? "");
                var file = Path.GetFileName(accFile);

                // Handle "...\\*" folder.
                if (file == "*")
                {
                    if (!Directory.Exists(Path.GetDirectoryName(folder)))
                        return true;
                    if (!Globals.RecursiveDelete(folder, false))
                        _ = GeneralInvocableFuncs.ShowToast("error", Lang["Platform_DeleteFail"], Lang["Error"], "toastarea"); ;
                    return true;
                }

                // Handle "...\\*.log" or "...\\file_*", etc.
                // This is NOT recursive - Specify folders manually in JSON
                if (!Directory.Exists(folder)) return true;
                foreach (var f in Directory.GetFiles(folder, file))
                    Globals.DeleteFile(f);

                return true;
            }

            var fullPath = ExpandEnvironmentVariables(accFile);
            // Is folder? Recursive copy folder
            if (Directory.Exists(fullPath))
            {
                if (!Globals.RecursiveDelete(fullPath, true))
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Platform_DeleteFail"], Lang["Error"], "toastarea");
                return true;
            }

            try
            {
                // Is file? Delete file
                Globals.DeleteFile(fullPath, true);
            }
            catch (UnauthorizedAccessException e)
            {
                Globals.WriteToLog(e);
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Platform_DeleteFail"], Lang["Error"], "toastarea");
            }
            return true;
        }

        /// <summary>
        /// Get string contents of registry file, or path to file matching regex/wildcards.
        /// </summary>
        /// <param name="accFile"></param>
        /// <param name="regex"></param>
        /// <returns></returns>
        private static string RegexSearchFileOrFolder(string accFile, string regex)
        {
            accFile = ExpandEnvironmentVariables(accFile);
            regex = Globals.ExpandRegex(regex);
            // The "file" is a registry key
            if (OperatingSystem.IsWindows() && accFile.StartsWith("REG:"))
            {
                var res = Globals.ReadRegistryKey(accFile);
                switch (res)
                {
                    case string:
                        return res;
                    case byte[] bytes:
                        return Globals.GetSha256HashString(bytes);
                    default:
                        Globals.WriteToLog($"REG was read, and was returned something that is not a string or byte array! {accFile}.");
                        Globals.WriteToLog("Check to see what is expected here and report to TechNobo.");
                        return res;
                }
            }


            // Handle wildcards
            if (accFile.Contains('*'))
            {
                var folder = ExpandEnvironmentVariables(Path.GetDirectoryName(accFile) ?? "");
                var file = Path.GetFileName(accFile);

                // Handle "...\\*" folder.
                // as well as "...\\*.log" or "...\\file_*", etc.
                // This is NOT recursive - Specify folders manually in JSON
                return Directory.Exists(folder) ? Globals.RegexSearchFolder(folder, regex, file) : "";
            }

            var fullPath = ExpandEnvironmentVariables(accFile);
            // Is folder? Search folder.
            if (Directory.Exists(fullPath))
                return Globals.RegexSearchFolder(fullPath, regex);

            // Is file? Search file
            var m = Regex.Match(File.ReadAllText(fullPath!), regex);
            return m.Success ? m.Value : "";
        }

        /// <summary>
        /// Expands custom environment variables.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="noIncludeBasicCheck">Whether to skip initializing BasicSettings - Useful for Steam and other hardcoded platforms</param>
        /// <returns></returns>
        public static string ExpandEnvironmentVariables(string path, bool noIncludeBasicCheck = false)
        {
            var variables = new Dictionary<string, string>()
            {
                { "%TCNO_UserData%", Globals.UserDataFolder },
                { "%TCNO_AppData%", Globals.AppDataFolder },
                { "%Documents%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                { "%Music%", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) },
                { "%Pictures%", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) },
                { "%Videos%", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) },
                { "%StartMenu%", Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) },
                { "%StartMenuProgramData%", Environment.ExpandEnvironmentVariables(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "\\Programs")) },
                { "%StartMenuAppData%", Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "\\Programs") }
            };
            if (!noIncludeBasicCheck)
                variables.Add("%Platform_Folder%", BasicSettings.FolderPath ?? "");

            foreach (var (k,v) in variables)
                path = path.Replace(k, v);

            return Environment.ExpandEnvironmentVariables(path);
        }

        private static bool BasicCopyInAccount(string accId)
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.BasicCopyInAccount]");
            LoadAccountIds();
            var accName = GetNameFromId(accId);

            var localCachePath = CurrentPlatform.AccountLoginCachePath(accName);
            _ = Directory.CreateDirectory(localCachePath);

            if (CurrentPlatform.LoginFiles == null) throw new Exception("No data in basic platform: " + CurrentPlatform.FullName);

            // Get unique ID from IDs file if unique ID is a registry key. Set if exists.
            if (OperatingSystem.IsWindows() && CurrentPlatform.UniqueIdMethod is "REGKEY" && !string.IsNullOrEmpty(CurrentPlatform.UniqueIdFile))
            {
                var uniqueId = GeneralFuncs.ReadDict(CurrentPlatform.SafeName).FirstOrDefault(x => x.Value == accName).Key;

                if (!string.IsNullOrEmpty(uniqueId) && !Globals.SetRegistryKey(CurrentPlatform.UniqueIdFile, uniqueId)) // Remove "REG:" and read data
                {
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_AlreadyLoggedIn"], Lang["Error"], "toastarea");
                    return false;
                }
            }

            var regJson = CurrentPlatform.HasRegistryFiles ? CurrentPlatform.ReadRegJson(accName) : new Dictionary<string, string>();

            foreach (var (accFile, savedFile) in CurrentPlatform.LoginFiles)
            {
                // The "file" is a registry key
                if (OperatingSystem.IsWindows() && accFile.StartsWith("REG:"))
                {
                    if (!regJson.ContainsKey(accFile))
                    {
                        _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailReadSaved"], Lang["Error"], "toastarea");
                        continue;
                    }

                    var regValue = regJson[accFile] ?? "";

                    if (!Globals.SetRegistryKey(accFile[4..], regValue)) // Remove "REG:" and read data
                    {
                        _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailWrite"], Lang["Error"], "toastarea");
                        return false;
                    }
                    continue;
                }

                // FILE OR FOLDER
                HandleFileOrFolder(accFile, savedFile, localCachePath, true);
            }

            return true;
        }

        private static bool KillGameProcesses()
        {
            // Kill game processes
            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_ClosingPlatform", new { platform = CurrentPlatform.FullName }]);
            if (!GeneralFuncs.CloseProcesses(CurrentPlatform.ExesToEnd, BasicSettings.ClosingMethod))
            {
                if (Globals.IsAdministrator)
                    _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_ClosingPlatformFailed", new { platform = CurrentPlatform.FullName }]);
                else
                {
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RestartAsAdmin"], Lang["Failed"], "toastarea");
                    _ = GeneralInvocableFuncs.ShowModal("notice:RestartAsAdmin");
                }
                return false;
            };

            return true;
        }

        [SupportedOSPlatform("windows")]
        public static bool BasicAddCurrent(string accName)
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.BasicAddCurrent]");
            if (CurrentPlatform.ExitBeforeInteract)
                if (!KillGameProcesses())
                    return false;

            // If set to clear LoginCache for account before adding (Enabled by default):
            if (CurrentPlatform.ClearLoginCache)
            {
                Globals.RecursiveDelete(CurrentPlatform.AccountLoginCachePath(accName), false);
            }

            // Separate special arguments (if any)
            var specialString = "";
            if (CurrentPlatform.HasExtras && accName.Contains(":{"))
            {
                var index = accName.IndexOf(":{")! + 1;
                specialString = accName[index..];
                accName = accName.Split(":{")[0];
            }

            var localCachePath = CurrentPlatform.AccountLoginCachePath(accName);
            _ = Directory.CreateDirectory(localCachePath);

            if (CurrentPlatform.LoginFiles == null) throw new Exception("No data in basic platform: " + CurrentPlatform.FullName);

            // Handle unique ID
            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_GetUniqueId"]);

            var uniqueId = "";
            if (CurrentPlatform.UniqueIdMethod is "REGKEY" && !string.IsNullOrEmpty(CurrentPlatform.UniqueIdFile))
            {
                if (!ReadRegistryKeyWithErrors(CurrentPlatform.UniqueIdFile, out var r))
                    return false;

                if (r is string s) uniqueId = s;
                else if (r is byte[]) uniqueId = Globals.GetSha256HashString(r);
                else
                {
                    Globals.WriteToLog($"Unexpected registry type encountered (1)! Report to TechNobo. {r.GetType()}");
                    return false;
                }

            }
            else
                uniqueId = GetUniqueId();

            if (uniqueId == "" && CurrentPlatform.UniqueIdMethod == "CREATE_ID_FILE")
            {
                // Unique ID file, and does not already exist: Therefore create!
                var uniqueIdFile = CurrentPlatform.GetUniqueFilePath();
                uniqueId = Globals.RandomString(16);
                File.WriteAllText(uniqueIdFile, uniqueId);
            }

            // Handle special args in username
            var hadSpecialProperties = ProcessSpecialAccName(specialString, accName, uniqueId);

            var regJson = CurrentPlatform.HasRegistryFiles ? CurrentPlatform.ReadRegJson(accName) : new Dictionary<string, string>();

            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_CopyingFiles"]);
            foreach (var (accFile, savedFile) in CurrentPlatform.LoginFiles)
            {
                // HANDLE REGISTRY KEY
                if (accFile.StartsWith("REG:"))
                {
                    var trimmedName = accFile[4..];

                    if (ReadRegistryKeyWithErrors(trimmedName, out var response)) // Remove "REG:" and read data
                    {
                        // Write registry value to provided file
                        if (response is string s) regJson[accFile] = s;
                        else if (response is byte[] ba) regJson[accFile] = "(hex) " + Globals.ByteArrayToString(ba);
                        else Globals.WriteToLog($"Unexpected registry type encountered (2)! Report to TechNobo. {response.GetType()}");
                    }
                    continue;
                }

                // FILE OR FOLDER
                if (HandleFileOrFolder(accFile, savedFile, localCachePath, false)) continue;

                // Could not find file/folder
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["CouldNotFindX", new { x = accFile }], Lang["DirectoryNotFound"], "toastarea");
                return false;

                // TODO: Run some action that can be specified in the Platforms.json file
                // Add for the start, and end of this function -- To allow 'plugins'?
                // Use reflection?
            }

            CurrentPlatform.SaveRegJson(regJson, accName);

            var allIds = GeneralFuncs.ReadDict(CurrentPlatform.IdsJsonPath);
            allIds[uniqueId] = accName;
            File.WriteAllText(CurrentPlatform.IdsJsonPath, JsonConvert.SerializeObject(allIds));

            // Copy in profile image from default -- As long as not already handled by special arguments
            // Or if has ProfilePicFromFile and ProfilePicRegex.
            if (!hadSpecialProperties.Contains("IMAGE|"))
            {
                _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_HandlingImage"]);

                _ = Directory.CreateDirectory(Path.Join(GeneralFuncs.WwwRoot(), $"\\img\\profiles\\{CurrentPlatform.SafeName}"));
                var profileImg = Path.Join(GeneralFuncs.WwwRoot(), $"\\img\\profiles\\{CurrentPlatform.SafeName}\\{Globals.GetCleanFilePath(uniqueId)}.jpg");
                if (!File.Exists(profileImg))
                {
                    var platformImgPath = "\\img\\platform\\" + CurrentPlatform.SafeName + "Default.png";

                    // Copy in profile picture (if found) from Regex search of files (if defined)
                    if (CurrentPlatform.ProfilePicFromFile != "" && CurrentPlatform.ProfilePicRegex != "")
                    {
                        var res = Globals.GetCleanFilePath(RegexSearchFileOrFolder(CurrentPlatform.ProfilePicFromFile, CurrentPlatform.ProfilePicRegex));
                        var sourcePath = res;
                        if (CurrentPlatform.ProfilePicPath != "")
                        {
                            // The regex result should be considered a filename.
                            // Sub in %FileName% from res, and %UniqueId% from uniqueId
                            sourcePath = ExpandEnvironmentVariables(CurrentPlatform.ProfilePicPath.Replace("%FileName%", res).Replace("%UniqueId", uniqueId));
                        }

                        if (res != "" && File.Exists(sourcePath))
                            if (!Globals.CopyFile(sourcePath, profileImg))
                                Globals.WriteToLog("Tried to save profile picture from path (ProfilePicFromFile, ProfilePicRegex method)");
                    }
                    else if (CurrentPlatform.ProfilePicPath != "")
                    {
                        var sourcePath = ExpandEnvironmentVariables(Globals.GetCleanFilePath(CurrentPlatform.ProfilePicPath.Replace("%UniqueId", uniqueId))) ?? "";
                        if (sourcePath != "" && File.Exists(sourcePath))
                            if (!Globals.CopyFile(sourcePath, profileImg))
                                Globals.WriteToLog("Tried to save profile picture from path (ProfilePicPath method)");
                    }

                    // Else (If file couldn't be saved, or not found -> Default.
                    if (!File.Exists(profileImg))
                    {
                        var currentPlatformImgPath = Path.Join(GeneralFuncs.WwwRoot(), platformImgPath);
                        Globals.CopyFile(File.Exists(currentPlatformImgPath)
                            ? Path.Join(currentPlatformImgPath)
                            : Path.Join(GeneralFuncs.WwwRoot(), "\\img\\BasicDefault.png"), profileImg);
                    }
                }
            }

            AppData.ActiveNavMan?.NavigateTo("/Basic/?cacheReload&toast_type=success&toast_title=Success&toast_message=" + Uri.EscapeDataString(Lang["Toast_SavedItem", new { item = accName }]), true);
            return true;
        }

        /// <summary>
        /// Handles copying files or folders around
        /// </summary>
        /// <param name="fromPath"></param>
        /// <param name="toPath"></param>
        /// <param name="localCachePath"></param>
        /// <param name="reverse">FALSE: Platform -> LoginCache. TRUE: LoginCache -> J••Platform</param>
        private static bool HandleFileOrFolder(string fromPath, string toPath, string localCachePath, bool reverse)
        {
            // Expand, or join localCachePath
            var toFullPath = toPath.Contains('%')
                ? ExpandEnvironmentVariables(toPath)
                : Path.Join(localCachePath, toPath);

            // Reverse if necessary. Explained in summary above.
            if (reverse && fromPath.Contains('*'))
            {
                (toPath, fromPath) = (fromPath, toPath); // Reverse
                var wildcard = Path.GetFileName(toPath);
                // Expand, or join localCachePath
                fromPath = fromPath.Contains('%')
                    ? ExpandEnvironmentVariables(Path.Join(fromPath, wildcard))
                    : Path.Join(localCachePath, fromPath, wildcard);
                toPath = toPath.Replace(wildcard, "");
                toFullPath = toPath;
            }

            // Handle wildcards
            if (fromPath.Contains('*'))
            {
                var folder = ExpandEnvironmentVariables(Path.GetDirectoryName(fromPath) ?? "");
                var file = Path.GetFileName(fromPath);

                // Handle "...\\*" folder.
                if (file == "*")
                {
                    if (!Directory.Exists(Path.GetDirectoryName(fromPath))) return false;
                    if (Globals.CopyFilesRecursive(Path.GetDirectoryName(fromPath), toFullPath, true)) return true;

                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_FileCopyFail"], renderTo: "toastarea");
                    return false;
                }

                // Handle "...\\*.log" or "...\\file_*", etc.
                // This is NOT recursive - Specify folders manually in JSON
                _ = Directory.CreateDirectory(folder);
                foreach (var f in Directory.GetFiles(folder, file))
                {
                    if (toFullPath == null) return false;
                    if (toFullPath.Contains('*')) toFullPath = Path.GetDirectoryName(toFullPath);
                    var fullOutputPath = Path.Join(toFullPath, Path.GetFileName(f));
                    Globals.CopyFile(f, fullOutputPath);
                }

                return true;
            }

            if (reverse)
                (fromPath, toFullPath) = (toFullPath, fromPath);

            var fullPath = ExpandEnvironmentVariables(fromPath);
            // Is folder? Recursive copy folder
            if (Directory.Exists(fullPath))
            {
                _ = Directory.CreateDirectory(toFullPath);
                if (Globals.CopyFilesRecursive(fullPath, toFullPath, true)) return true;
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_FileCopyFail"], renderTo: "toastarea");
                return false;
            }

            // Is file? Copy file
            if (!File.Exists(fullPath)) return false;
            _ = Directory.CreateDirectory(Path.GetDirectoryName(toFullPath));
            var dest = Path.Join(Path.GetDirectoryName(toFullPath), Path.GetFileName(fullPath));
            Globals.CopyFile(fullPath, dest);
            return true;

        }

        /// <summary>
        /// Do special actions with AccName, and return cleaned AccName when done.
        /// </summary>
        /// <param name="accName">Account Name:{JSON OBJECT}</param>
        /// <param name="uniqueId">Unique ID of account</param>
        /// <param name="jsonString">JSON string of actions to perform on account</param>
        private static string ProcessSpecialAccName(string jsonString, string accName, string uniqueId)
        {
            // Verify existence of possible extra properties
            var hadSpecialProperties = "";
            if (!CurrentPlatform.HasExtras) return hadSpecialProperties;
            var specialProperties = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString);
            if (specialProperties == null) return hadSpecialProperties;

            // HANDLE SPECIAL IMAGE
            var profileImg = Path.Join(GeneralFuncs.WwwRoot(), $"\\img\\profiles\\{CurrentPlatform.SafeName}\\{Globals.GetCleanFilePath(uniqueId)}.jpg");
            if (specialProperties.ContainsKey("image"))
            {
                var imageIsUrl = Uri.TryCreate(specialProperties["image"], UriKind.Absolute, out var uriResult)
                                 && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
                if (imageIsUrl)
                {
                    // Is url -> Download
                    if (Globals.DownloadFile(specialProperties["image"], profileImg))
                        hadSpecialProperties = "IMAGE|";
                }
                else
                {
                    // Is not url -> Copy file
                    if (Globals.CopyFile(ExpandEnvironmentVariables(specialProperties["image"]), profileImg))
                        hadSpecialProperties = "IMAGE|";
                }
            }

            return hadSpecialProperties;
        }

        public static string GetUniqueId()
        {
            var fileToRead = CurrentPlatform.GetUniqueFilePath();
            var uniqueId = "";

            if (CurrentPlatform.UniqueIdMethod is "REGKEY")
            {
                _ = ReadRegistryKeyWithErrors(CurrentPlatform.UniqueIdFile, out var r);
                if (r is string) uniqueId = r;
                else if (r is byte[] ba) uniqueId = Globals.GetSha256HashString(ba);
                else Globals.WriteToLog($"Unexpected registry type encountered (3)! Report to TechNobo. {r.GetType()}");
                return uniqueId;
            }

            if (CurrentPlatform.UniqueIdMethod is "CREATE_ID_FILE")
            {
                return File.Exists(fileToRead) ? File.ReadAllText(fileToRead) : uniqueId;
            }

            if (CurrentPlatform.UniqueIdFile is not "" && (File.Exists(fileToRead) || fileToRead.Contains('*')))
            {
                if (!string.IsNullOrEmpty(CurrentPlatform.UniqueIdRegex))
                {
                    uniqueId = Globals.GetCleanFilePath(RegexSearchFileOrFolder(fileToRead, CurrentPlatform.UniqueIdRegex)); // Get unique ID from Regex, but replace any illegal characters.
                }
                else if (CurrentPlatform.UniqueIdMethod is "FILE_MD5") // TODO: TEST THIS! -- This is used for static files that do not change throughout the lifetime of an account login.
                {
                    if (fileToRead.Contains('*'))
                        uniqueId = GeneralFuncs.GetFileMd5(Directory.GetFiles(Path.GetDirectoryName(fileToRead), Path.GetFileName(fileToRead)).First());
                    else
                        uniqueId = GeneralFuncs.GetFileMd5(fileToRead);
                }
            }
            else if (uniqueId != "")
                uniqueId = Globals.GetSha256HashString(uniqueId);

            return uniqueId;
        }

        private static bool ReadRegistryKeyWithErrors(string key, out dynamic value)
        {
            value = Globals.ReadRegistryKey(key);
            switch (value)
            {
                case "ERROR-NULL":
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_AccountIdReg"], Lang["Error"], "toastarea");
                    return false;
                case "ERROR-READ":
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailRead"], Lang["Error"], "toastarea");
                    return false;
            }

            return true;
        }
        public static void ChangeUsername(string accId, string newName, bool reload = true)
        {
            LoadAccountIds();
            var oldName = GetNameFromId(accId);

            try
            {
                // No need to rename image as accId. That step is skipped here.
                Directory.Move($"LoginCache\\{CurrentPlatform.SafeName}\\{oldName}\\", $"LoginCache\\{CurrentPlatform.SafeName}\\{newName}\\"); // Rename login cache folder
            }
            catch (IOException e)
            {
                Globals.WriteToLog("Failed to write to file: " + e);
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Error_FileAccessDenied", new { logPath = Globals.GetLogPath() }], Lang["Error"], "toastarea");
                return;
            }

            try
            {
                AccountIds[accId] = newName;
                SaveAccountIds();
            }
            catch (Exception e)
            {
                Globals.WriteToLog("Failed to change username: " + e);
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_CantChangeUsername"], Lang["Error"], "toastarea");
                return;
            }


            if (reload) AppData.ActiveNavMan?.NavigateTo("/Basic/?cacheReload&toast_type=success&toast_title=Success&toast_message=" + Uri.EscapeDataString(Lang["Toast_ChangedUsername"]), true);
        }

        public static Dictionary<string, string> ReadAllIds(string path = null)
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.ReadAllIds]");
            var s = JsonConvert.SerializeObject(new Dictionary<string, string>());
            path ??= CurrentPlatform.IdsJsonPath;
            if (!File.Exists(path)) return JsonConvert.DeserializeObject<Dictionary<string, string>>(s);
            try
            {
                s = Globals.ReadAllText(path);
            }
            catch (Exception)
            {
                //
            }

            return JsonConvert.DeserializeObject<Dictionary<string, string>>(s);
        }
    }
}
