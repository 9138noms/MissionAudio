using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.ObjectiveV2;
using NuclearOption.SavedMission.ObjectiveV2.Outcomes;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace MissionAudio
{
    [Serializable]
    public class AudioEntry
    {
        public string outcome;
        public string file;
        public float volume = 1f;
    }

    [Serializable]
    public class AudioConfig
    {
        public List<AudioEntry> entries = new List<AudioEntry>();
    }

    // Dedicated persistent MonoBehaviour — survives scene changes regardless of
    // what BepInEx does to the plugin GO. Lazily (re)created on demand.
    public class MissionAudioHost : MonoBehaviour
    {
        Mission lastSeen;
        void Update()
        {
            // Catch mission unloads even if onMissionLoad(NullMission) isn't raised.
            // Only stop audio; don't clear AudioMap/Folder — that breaks editor state
            // when the editor momentarily sets CurrentMission=null between panels.
            var m = MissionManager.CurrentMission;
            if (!ReferenceEquals(m, lastSeen))
            {
                lastSeen = m;
                if (m == null || m == Mission.NullMission) Plugin.StopAll();
            }
        }
    }

    [BepInPlugin("com.noms.missionaudio", "MissionAudio", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string OUTCOME_PREFIX = "__MA__";
        internal const string MENU_OPTION = "Play Audio";

        internal static ManualLogSource Log;

        static MissionAudioHost _host;
        static AudioSource _source;

        internal static MissionAudioHost Host
        {
            get
            {
                if (_host == null)
                {
                    var go = new GameObject("MissionAudio_Host");
                    GameObject.DontDestroyOnLoad(go);
                    _host = go.AddComponent<MissionAudioHost>();
                    _source = go.AddComponent<AudioSource>();
                    _source.playOnAwake = false;
                    _source.spatialBlend = 0f;
                    Log?.LogDebug("host GO created");
                }
                return _host;
            }
        }

        internal static AudioSource Source
        {
            get { _ = Host; return _source; }
        }

        internal static string AudioLibraryDir;
        internal static readonly List<string> AudioLibrary = new List<string>();
        internal static readonly Dictionary<string, AudioClip> ClipCache = new Dictionary<string, AudioClip>();

        internal static readonly Dictionary<string, AudioEntry> AudioMap =
            new Dictionary<string, AudioEntry>(StringComparer.Ordinal);

        internal static string CurrentMissionFolder;

        void Awake()
        {
            Log = Logger;

            var dllDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            AudioLibraryDir = Path.Combine(dllDir, "audio");
            if (!Directory.Exists(AudioLibraryDir))
            {
                Directory.CreateDirectory(AudioLibraryDir);
                File.WriteAllText(
                    Path.Combine(AudioLibraryDir, "README.txt"),
                    "Drop .mp3 / .wav / .ogg / .aiff files here.\n" +
                    "They'll appear in the mission editor under Outcomes → 'Play Audio'.\n");
            }
            RefreshLibrary();

            _ = Host; // force create persistent host + source

            MissionManager.onMissionLoad += OnMissionLoad;

            new Harmony("com.noms.missionaudio").PatchAll();
            Log.LogInfo($"MissionAudio v1.0.0 loaded. Library: {AudioLibraryDir} ({AudioLibrary.Count} files)");
        }

        internal static void RefreshLibrary()
        {
            AudioLibrary.Clear();
            if (!Directory.Exists(AudioLibraryDir)) return;
            var exts = new[] { ".mp3", ".wav", ".ogg", ".aif", ".aiff" };
            foreach (var f in Directory.GetFiles(AudioLibraryDir))
            {
                if (exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    AudioLibrary.Add(Path.GetFileName(f));
            }
            AudioLibrary.Sort(StringComparer.OrdinalIgnoreCase);
        }

        void OnMissionLoad(Mission mission) => LoadFor(mission);

        internal static void LoadFor(Mission mission)
        {
            // Stop any currently-playing audio on mission change / unload
            StopAll();

            AudioMap.Clear();
            CurrentMissionFolder = null;

            if (mission == null || mission == Mission.NullMission)
            {
                Log?.LogDebug("mission unloaded, audio stopped");
                return;
            }

            CurrentMissionFolder = ResolveFolder(mission);
            Log?.LogDebug($"LoadFor mission='{mission.Name}' folder='{CurrentMissionFolder ?? "null"}'");
            if (string.IsNullOrEmpty(CurrentMissionFolder)) return;

            string cfgPath = Path.Combine(CurrentMissionFolder, "audio.json");
            if (File.Exists(cfgPath))
            {
                // Drop invalid/stale audio.json so fallback auto-create can work cleanly
                try
                {
                    var raw = File.ReadAllText(cfgPath).Trim();
                    if (string.IsNullOrEmpty(raw) || raw == "{}" || !raw.Contains("entries"))
                    {
                        File.Delete(cfgPath);
                        Log?.LogWarning($"MissionAudio: deleted invalid audio.json (was: {raw})");
                    }
                }
                catch { /* ignore */ }
            }

            if (!File.Exists(cfgPath))
            {
                Log?.LogDebug($"no audio.json at {cfgPath}");
            }
            else try
            {
                var parsed = ManualJsonParse(File.ReadAllText(cfgPath));
                foreach (var e in parsed)
                {
                    if (string.IsNullOrEmpty(e.outcome)) continue;
                    AudioMap[e.outcome] = new AudioEntry
                    {
                        outcome = e.outcome,
                        file = e.file ?? "",
                        volume = e.volume <= 0f ? 1f : Mathf.Clamp(e.volume, 0f, 2f),
                    };
                }
                Log?.LogInfo($"MissionAudio: loaded {AudioMap.Count} audio outcome(s) from {cfgPath}");
            }
            catch (Exception e) { Log?.LogWarning($"audio.json parse error: {e.Message}"); }

            // Fallback: scan mission outcomes for __MA__ that are missing from audio.json
            try
            {
                var objectives = MissionManager.Objectives;
                if (objectives != null)
                {
                    int autoCreated = 0;
                    foreach (var outcome in objectives.AllOutcomes ?? Enumerable.Empty<Outcome>())
                    {
                        var uname = outcome?.SavedOutcome.UniqueName;
                        if (uname == null || !uname.StartsWith(OUTCOME_PREFIX, StringComparison.Ordinal)) continue;
                        if (AudioMap.ContainsKey(uname)) continue;
                        AudioMap[uname] = new AudioEntry
                        {
                            outcome = uname,
                            file = AudioLibrary.Count > 0 ? AudioLibrary[0] : "",
                            volume = 1f
                        };
                        autoCreated++;
                    }
                    if (autoCreated > 0)
                    {
                        var def = AudioLibrary.Count > 0 ? AudioLibrary[0] : "(none)";
                        Log?.LogWarning($"MissionAudio: auto-created {autoCreated} entry/entries (defaulted to '{def}'). Open each outcome in the mission editor to pick the right file.");
                        SaveSidecarTo(CurrentMissionFolder);
                    }
                }
            }
            catch (Exception e) { Log?.LogWarning($"MissionAudio auto-recovery: {e.Message}"); }

            foreach (var entry in AudioMap.Values)
                if (!string.IsNullOrEmpty(entry.file)) Host.StartCoroutine(EnsureClipLoaded(entry.file));
        }

        static string ResolveFolder(Mission m)
        {
            if (m.LoadKey.HasValue)
            {
                var keyStr = m.LoadKey.Value.Key;
                if (!string.IsNullOrEmpty(keyStr) && Path.IsPathRooted(keyStr) && Directory.Exists(keyStr))
                    return keyStr;
            }
            string name = m.LoadKey?.Name ?? m.Name;
            if (string.IsNullOrEmpty(name)) return null;

            string user = Path.Combine(Application.persistentDataPath, "Missions", name);
            if (Directory.Exists(user)) return user;

            string temp = Path.Combine(Application.persistentDataPath, "TempMissions", name);
            if (Directory.Exists(temp)) return temp;
            return null;
        }

        internal static IEnumerator EnsureClipLoaded(string file)
        {
            if (string.IsNullOrEmpty(file) || ClipCache.ContainsKey(file)) yield break;
            string full = Path.Combine(AudioLibraryDir, file);
            if (!File.Exists(full))
            {
                Log.LogWarning($"MissionAudio: file not in library: {file}");
                yield break;
            }
            AudioType type;
            switch (Path.GetExtension(full).ToLowerInvariant())
            {
                case ".mp3": type = AudioType.MPEG; break;
                case ".wav": type = AudioType.WAV; break;
                case ".ogg": type = AudioType.OGGVORBIS; break;
                case ".aif":
                case ".aiff": type = AudioType.AIFF; break;
                default: type = AudioType.UNKNOWN; break;
            }
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(full).AbsoluteUri, type))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Log.LogWarning($"MissionAudio: load failed '{file}' ({req.error})");
                    yield break;
                }
                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip != null)
                {
                    clip.name = file;
                    ClipCache[file] = clip;
                    Log.LogDebug($"cached '{file}' ({clip.length:F1}s)");
                }
            }
        }

        internal static void PlayEntry(AudioEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.file))
            {
                Log?.LogWarning($"MissionAudio: PlayEntry skipped (entry or file empty)");
                return;
            }
            if (!ClipCache.TryGetValue(e.file, out var clip) || clip == null)
            {
                Log?.LogDebug($"clip not cached, loading '{e.file}'");
                Host.StartCoroutine(PlayDelayed(e));
                return;
            }
            PlayClipNow(clip, e.volume);
            Log?.LogInfo($"MissionAudio: played '{e.file}' vol={e.volume}");
        }

        static void PlayClipNow(AudioClip clip, float volume)
        {
            var s = Source;
            if (s == null) return;
            s.Stop();
            s.clip = clip;
            s.volume = volume;
            s.Play();
        }

        internal static void StopAll()
        {
            try
            {
                if (_source != null)
                {
                    _source.Stop();
                    _source.clip = null;
                }
            }
            catch (Exception e) { Log?.LogWarning($"StopAll: {e.Message}"); }
        }

        static IEnumerator PlayDelayed(AudioEntry e)
        {
            yield return EnsureClipLoaded(e.file);
            if (ClipCache.TryGetValue(e.file, out var clip) && clip != null)
            {
                PlayClipNow(clip, e.volume);
                Log?.LogInfo($"MissionAudio: played '{e.file}' vol={e.volume} (after load)");
            }
            else
            {
                Log?.LogWarning($"MissionAudio: PlayDelayed still no clip for '{e.file}'");
            }
        }

        internal static AudioEntry GetOrCreateEntry(string outcomeUniqueName)
        {
            if (!AudioMap.TryGetValue(outcomeUniqueName, out var e))
            {
                e = new AudioEntry
                {
                    outcome = outcomeUniqueName,
                    file = AudioLibrary.Count > 0 ? AudioLibrary[0] : "",
                    volume = 1f
                };
                AudioMap[outcomeUniqueName] = e;
            }
            return e;
        }

        internal static void SaveSidecar()
        {
            if (string.IsNullOrEmpty(CurrentMissionFolder))
            {
                // Try to resolve lazily from current mission
                var m = MissionManager.CurrentMission;
                if (m != null && m != Mission.NullMission)
                {
                    CurrentMissionFolder = ResolveFolder(m);
                }
            }
            if (string.IsNullOrEmpty(CurrentMissionFolder))
            {
                Log?.LogWarning("MissionAudio: SaveSidecar skipped — no current mission folder");
                return;
            }
            SaveSidecarTo(CurrentMissionFolder);
        }

        internal static void SaveSidecarTo(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                var entries = AudioMap.Values.ToList();
                string path = Path.Combine(folder, "audio.json");
                if (entries.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        long existingLen = new FileInfo(path).Length;
                        if (existingLen > 3)
                        {
                            Log?.LogDebug($"SaveSidecarTo skipped (map empty, existing file {existingLen} bytes preserved)");
                            return;
                        }
                    }
                    Log?.LogDebug($"SaveSidecarTo noop (map empty, no file)");
                    return;
                }
                string json = ManualJsonWrite(entries);
                File.WriteAllText(path, json);
                Log?.LogDebug($"SaveSidecarTo wrote {entries.Count} entry/entries to {path} ({json.Length} bytes)");
            }
            catch (Exception e) { Log?.LogWarning($"MissionAudio save: {e.Message}"); }
        }

        static string ManualJsonWrite(List<AudioEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"entries\": [\n");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("    {");
                sb.Append("\"outcome\":\"").Append(JsonEscape(e.outcome)).Append("\",");
                sb.Append("\"file\":\"").Append(JsonEscape(e.file)).Append("\",");
                sb.Append("\"volume\":").Append(e.volume.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(i < entries.Count - 1 ? "},\n" : "}\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static List<AudioEntry> ManualJsonParse(string json)
        {
            var list = new List<AudioEntry>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            // Match each object inside entries array
            var objRx = new Regex(@"\{\s*""outcome""\s*:\s*""([^""]*)""\s*,\s*""file""\s*:\s*""([^""]*)""\s*,\s*""volume""\s*:\s*([-\d.]+)\s*\}",
                                  RegexOptions.Multiline);
            foreach (Match m in objRx.Matches(json))
            {
                list.Add(new AudioEntry
                {
                    outcome = m.Groups[1].Value,
                    file = m.Groups[2].Value,
                    volume = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
                });
            }
            return list;
        }

        internal static bool IsAudioOutcome(Outcome o)
        {
            return o is ShowMessageOutcome && o.SavedOutcome.UniqueName != null &&
                   o.SavedOutcome.UniqueName.StartsWith(OUTCOME_PREFIX, StringComparison.Ordinal);
        }
    }

    // ---- Patch: add "Play Audio" option to outcome-type dropdown ----
    // Start/Create are private methods on the generic base NewPanelBase<OutcomeType>,
    // not on NewOutcomePanel itself. Must target the base type explicitly.
    [HarmonyPatch]
    internal static class Patch_NewOutcomePanel_Start
    {
        static readonly Type BaseT = typeof(NewPanelBase<OutcomeType>);

        static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(BaseT, "Start");

        static void Postfix(NewPanelBase<OutcomeType> __instance)
        {
            try
            {
                if (!(__instance is NewOutcomePanel)) return;
                var options = (List<string>)AccessTools.Field(BaseT, "options").GetValue(__instance);
                var dd = (TMP_Dropdown)AccessTools.Field(BaseT, "typeDropdown").GetValue(__instance);
                if (options == null || dd == null) return;
                if (!options.Contains(Plugin.MENU_OPTION))
                {
                    options.Add(Plugin.MENU_OPTION);
                    dd.AddOptions(new List<string> { Plugin.MENU_OPTION });
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"NewOutcomePanel.Start patch: {e.Message}"); }
        }
    }

    // ---- Patch: intercept Create when "Play Audio" selected ----
    [HarmonyPatch]
    internal static class Patch_NewOutcomePanel_Create
    {
        static readonly Type BaseT = typeof(NewPanelBase<OutcomeType>);

        static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(BaseT, "Create");

        static bool Prefix(NewPanelBase<OutcomeType> __instance)
        {
            try
            {
                if (!(__instance is NewOutcomePanel panel)) return true;
                var options = (List<string>)AccessTools.Field(BaseT, "options").GetValue(__instance);
                var dd = (TMP_Dropdown)AccessTools.Field(BaseT, "typeDropdown").GetValue(__instance);
                if (options == null || dd == null) return true;
                if (dd.value < 0 || dd.value >= options.Count) return true;
                if (options[dd.value] != Plugin.MENU_OPTION) return true;

                var nameField = (TMP_InputField)AccessTools.Field(BaseT, "nameField").GetValue(__instance);
                string baseName = string.IsNullOrWhiteSpace(nameField?.text) ? "audio" : nameField.text.Trim();
                string uname = Plugin.OUTCOME_PREFIX + baseName;

                var outcome = new ShowMessageOutcome();
                outcome.LoadNew(new SavedOutcome(uname, OutcomeType.ShowMessage));
                outcome.Message = "";
                outcome.PlaySound.SetValue(false, null);

                var parentObjective = (Objective)AccessTools.Field(typeof(NewOutcomePanel), "parentObjective").GetValue(panel);
                MissionManager.Objectives.AddNewOutcome(outcome, parentObjective);
                MissionManager.Objectives.MakeUnique(outcome);

                Plugin.GetOrCreateEntry(outcome.SavedOutcome.UniqueName);
                Plugin.SaveSidecar();

                var editor = SceneSingleton<MissionEditor>.i;
                editor?.CheckAutoSave();

                var sidePanel = panel.Panel;
                sidePanel?.Parent?.Refresh();

                var editPrefab = (EditOutcomePanel)AccessTools.Field(typeof(NewOutcomePanel), "editOutcomePanelPrefab").GetValue(panel);
                if (sidePanel != null && editPrefab != null)
                    sidePanel.Create(editPrefab).SetOutcome(outcome, parentObjective);
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"NewOutcomePanel.Create patch: {e.Message}"); }
            return false;
        }
    }

    // ---- Patch: replace ShowMessageOutcome edit UI for __MA__ outcomes ----
    [HarmonyPatch(typeof(ShowMessageOutcome), nameof(ShowMessageOutcome.DrawData))]
    internal static class Patch_ShowMessage_DrawData
    {
        static bool Prefix(ShowMessageOutcome __instance, DataDrawer drawer)
        {
            if (!Plugin.IsAudioOutcome(__instance)) return true;
            try
            {
                string uname = __instance.SavedOutcome.UniqueName;
                var entry = Plugin.GetOrCreateEntry(uname);

                drawer.DrawHeader("Audio Outcome");

                Plugin.RefreshLibrary();
                var files = Plugin.AudioLibrary;

                if (files.Count == 0)
                {
                    drawer.DrawLabelWarning("No audio files in " + Plugin.AudioLibraryDir);
                    drawer.DrawLabel("Drop .mp3/.wav/.ogg there, reopen editor.");
                }
                else
                {
                    int idx = files.IndexOf(entry.file);
                    if (idx < 0)
                    {
                        // First open / empty entry: pick files[0] as default and persist
                        idx = 0;
                        entry.file = files[0];
                        Plugin.SaveSidecar();
                    }
                    Plugin.Log?.LogDebug($"DrawData for '{uname}' current='{entry.file}' idx={idx}");
                    drawer.DrawDropdown("Audio File", files, idx, i =>
                    {
                        string oldFile = entry.file;
                        entry.file = files[i];
                        Plugin.Log?.LogDebug($"dropdown picked [{i}]='{entry.file}' for '{uname}' (was '{oldFile}')");
                        Plugin.SaveSidecar();
                        Plugin.Host.StartCoroutine(Plugin.EnsureClipLoaded(entry.file));
                    });
                }

                drawer.InstantiateWithParent(drawer.Prefabs.StringFieldPrefab)
                    .Setup("Volume (0~2)", entry.volume.ToString("0.##", CultureInfo.InvariantCulture),
                        v =>
                        {
                            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                            {
                                entry.volume = Mathf.Clamp(f, 0f, 2f);
                                Plugin.SaveSidecar();
                            }
                        });

                drawer.DrawLabel("Preview requires running the mission.");
                drawer.Success = true;
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"ShowMessage.DrawData patch: {e.Message}"); }
            return false;
        }
    }

    // ---- Patch: play audio instead of ShowMessage for __MA__ outcomes ----
    [HarmonyPatch(typeof(ShowMessageOutcome), nameof(ShowMessageOutcome.Complete))]
    internal static class Patch_ShowMessage_Complete
    {
        static bool Prefix(ShowMessageOutcome __instance, Objective completedObjective)
        {
            if (!Plugin.IsAudioOutcome(__instance)) return true;
            string uname = __instance.SavedOutcome.UniqueName;
            try
            {
                Plugin.Log?.LogDebug($"Complete fired for '{uname}' (map size={Plugin.AudioMap.Count}, folder={Plugin.CurrentMissionFolder ?? "null"})");
                if (Plugin.AudioMap.TryGetValue(uname, out var e))
                {
                    Plugin.Log?.LogDebug($"entry file='{e.file}' vol={e.volume} cached={Plugin.ClipCache.ContainsKey(e.file ?? "")}");
                    Plugin.PlayEntry(e);
                }
                else
                {
                    Plugin.Log?.LogWarning($"MissionAudio: no entry for '{uname}'. Keys: [{string.Join(",", Plugin.AudioMap.Keys)}]");
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning($"ShowMessage.Complete patch for '{uname}': {ex}"); }
            return false;
        }
    }

    // ---- Patch: also force-load audio.json on StartMission (backup for onMissionLoad) ----
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMission))]
    internal static class Patch_MissionManager_StartMission
    {
        static void Postfix()
        {
            try { Plugin.LoadFor(MissionManager.CurrentMission); }
            catch (Exception e) { Plugin.Log?.LogWarning($"StartMission patch: {e.Message}"); }
        }
    }

    // ---- Patch: persist sidecar on mission save ----
    [HarmonyPatch(typeof(MissionGroup.UserGroup), "SaveMission")]
    internal static class Patch_UserGroup_SaveMission
    {
        static void Postfix(string name, Mission mission)
        {
            try
            {
                string folder = MissionGroup.UserGroup.GetFolder(name);
                if (string.IsNullOrEmpty(Plugin.CurrentMissionFolder)) Plugin.CurrentMissionFolder = folder;
                Plugin.Log?.LogDebug($"SaveMission postfix name='{name}' mapSize={Plugin.AudioMap.Count}");
                Plugin.SaveSidecarTo(folder);
                // NOTE: no stale-entry cleanup — orphaned entries are harmless,
                // and AllOutcomes may not include outcomes mid-edit.
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"SaveMission patch: {e.Message}"); }
        }
    }
}
