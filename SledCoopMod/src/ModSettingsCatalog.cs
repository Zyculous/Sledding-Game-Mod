using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SledCoopMod
{
    // Declarative list of every ConfigEntry exposed in the in-game settings UI
    // plus the static rendering logic that walks the list. Rendering lives
    // here (not on ModSettingsUi) so the MonoBehaviour never has to declare
    // an Item-parameter method — Il2CppInterop emits "unsupported parameter
    // type" warnings for those, which on this Unity 6 IL2CPP build can break
    // method dispatch on the registered class.
    internal static class ModSettingsCatalog
    {
        internal sealed class Item
        {
            public Item(string section, string label, IConfigEntryWrapper entry, string? note = null)
            {
                Section = section;
                Label = label;
                Entry = entry;
                Note = note;
            }

            public string Section { get; }
            public string Label { get; }
            public IConfigEntryWrapper Entry { get; }
            public string? Note { get; }
        }

        internal interface IConfigEntryWrapper
        {
            string Description { get; }
            object? Get();
            void Set(object? value);
            Type ValueType { get; }
            AcceptableValueBase? AcceptableValues { get; }
        }

        internal sealed class ConfigEntryWrapper<T> : IConfigEntryWrapper
        {
            private readonly ConfigEntry<T> _entry;

            public ConfigEntryWrapper(ConfigEntry<T> entry) => _entry = entry;

            public string Description => _entry.Description?.Description ?? "";
            public Type ValueType => typeof(T);
            public AcceptableValueBase? AcceptableValues => _entry.Description?.AcceptableValues;

            public object? Get() => _entry.Value;

            public void Set(object? value)
            {
                if (value is T typed)
                    _entry.Value = typed;
                else if (value is string str && typeof(T) == typeof(string))
                    _entry.Value = (T)(object)str;
            }
        }

        internal static IConfigEntryWrapper Wrap<T>(ConfigEntry<T> entry) =>
            new ConfigEntryWrapper<T>(entry);

        internal static List<Item> Build()
        {
            return new List<Item>
            {
                new Item("General",       "Enabled (master)",         Wrap(ModConfig.Enabled),                  "Restart required"),

                new Item("Local co-op",   "LocalCoopEnabled",         Wrap(ModConfig.LocalCoopEnabled)),
                new Item("Local co-op",   "Max local players",        Wrap(ModConfig.MaxLocalPlayers),          "Restart required"),
                new Item("Local co-op",   "MidSessionJoinEnabled",    Wrap(ModConfig.MidSessionJoinEnabled)),

                new Item("Split-screen",  "2-player orientation",     Wrap(ModConfig.TwoPlayerSplitOrientation)),
                new Item("Split-screen",  "3-player layout",          Wrap(ModConfig.ThreePlayerLayout)),

                new Item("Multi-display", "MultiDisplayEnabled",      Wrap(ModConfig.MultiDisplayEnabled)),

                new Item("Networked",     "Enabled",                  Wrap(ModConfig.NetworkedInstancesEnabled), "Restart required"),
                new Item("Networked",     "LaunchChildProcesses",     Wrap(ModConfig.LaunchChildProcesses)),
                new Item("Networked",     "Port",                     Wrap(ModConfig.NetworkedInstancePort),     "Restart required"),

                new Item("Online",        "OnlineHybridEnabled",      Wrap(ModConfig.OnlineHybridEnabled),       "Experimental"),

                new Item("Debug",         "VerboseLogging",           Wrap(ModConfig.VerboseLogging)),
                new Item("Debug",         "Middle-click inspector",   Wrap(ModConfig.DebugInspectEnabled)),
                new Item("Debug",         "Outline all UI",           Wrap(ModConfig.DebugOutlineAllUi)),
                new Item("Debug",         "Force cursor lock",        Wrap(ModConfig.ForceCursorLockInGameplay)),

                new Item("Hotkey",        "Open/close key",           Wrap(ModConfig.SettingsHotkey)),
                new Item("Hotkey",        "Modifier",                 Wrap(ModConfig.SettingsHotkeyModifier)),
            };
        }

        internal static void RenderAll(ModSettingsUi host)
        {
            string? lastSection = null;
            foreach (var item in Build())
            {
                if (item.Section != lastSection)
                {
                    GUILayout.Space(8f);
                    GUILayout.Label(item.Section, host.Section);
                    lastSection = item.Section;
                }

                RenderRow(host, item);
            }
        }

        private static void RenderRow(ModSettingsUi host, Item item)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(item.Label, host.Label, GUILayout.Width(220f));
            RenderControl(host, item);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(item.Note))
            {
                if (item.Note == "Restart required") host.NoteRestartRequired();
                GUILayout.Label(item.Note == "Restart required" ? "*" : item.Note, host.Note, GUILayout.Width(80f));
            }
            GUILayout.EndHorizontal();
        }

        private static void RenderControl(ModSettingsUi host, Item item)
        {
            object? value = item.Entry.Get();
            Type t = item.Entry.ValueType;

            if (t == typeof(bool))
            {
                bool b = value is bool bv && bv;
                bool nb = GUILayout.Toggle(b, b ? "ON " : "OFF", host.Toggle_, GUILayout.Width(60f));
                if (nb != b) item.Entry.Set(nb);
                return;
            }

            if (t == typeof(int))
            {
                int iv = value is int ix ? ix : 0;
                int min = 0, max = 65535;
                if (item.Entry.AcceptableValues is AcceptableValueRange<int> range)
                {
                    min = range.MinValue;
                    max = range.MaxValue;
                }

                if (max - min <= 16)
                {
                    if (GUILayout.Button("-", host.Button, GUILayout.Width(28f)) && iv > min) item.Entry.Set(iv - 1);
                    GUILayout.Label(iv.ToString(), host.Label, GUILayout.Width(40f));
                    if (GUILayout.Button("+", host.Button, GUILayout.Width(28f)) && iv < max) item.Entry.Set(iv + 1);
                }
                else
                {
                    string current = iv.ToString();
                    string edited = GUILayout.TextField(current, 6, host.TextField_, GUILayout.Width(80f));
                    if (edited != current && int.TryParse(edited, out int parsed))
                        item.Entry.Set(Mathf.Clamp(parsed, min, max));
                }
                return;
            }

            if (t == typeof(float))
            {
                float fv = value is float fx ? fx : 0f;
                string current = fv.ToString("0.###");
                string edited = GUILayout.TextField(current, 8, host.TextField_, GUILayout.Width(80f));
                if (edited != current && float.TryParse(edited, out float parsed))
                    item.Entry.Set(parsed);
                return;
            }

            if (t == typeof(string))
            {
                string sv = value as string ?? "";

                if (item.Label == "Open/close key")
                {
                    string label = host.IsRebindArmed(item.Label) ? "Press a key…" : sv;
                    if (GUILayout.Button(label, host.Button, GUILayout.Width(140f)))
                        host.ArmRebind(item.Label);
                    return;
                }

                if (item.Entry.AcceptableValues is AcceptableValueList<string> list)
                {
                    string[] options = list.AcceptableValues;
                    int idx = Array.IndexOf(options, sv);
                    if (idx < 0) idx = 0;

                    if (GUILayout.Button("<", host.Button, GUILayout.Width(28f)))
                        idx = (idx - 1 + options.Length) % options.Length;
                    GUILayout.Label(options[idx], host.Label, GUILayout.Width(140f));
                    if (GUILayout.Button(">", host.Button, GUILayout.Width(28f)))
                        idx = (idx + 1) % options.Length;

                    if (options[idx] != sv) item.Entry.Set(options[idx]);
                    return;
                }

                string editedStr = GUILayout.TextField(sv, host.TextField_, GUILayout.Width(180f));
                if (editedStr != sv) item.Entry.Set(editedStr);
                return;
            }
        }
    }
}
