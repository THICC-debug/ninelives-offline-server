using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ScriptLoader
{
    /// <summary>
    /// The dialogue box answers to 1-9, Esc, Enter and Space. The game uses the same keys (hotbar slots,
    /// menu, jump): while a dialogue is open, the game doesn't see them.
    /// </summary>
    [HarmonyPatch]
    static class DialogKeys
    {
        static readonly HashSet<KeyCode> Ours = new HashSet<KeyCode> { KeyCode.Escape, KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Space };
        static DialogKeys() { for (int k = 0; k < 9; k++) { Ours.Add(KeyCode.Alpha1 + k); Ours.Add(KeyCode.Keypad1 + k); } }

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InputController), nameof(InputController.GetKey), new[] { typeof(string) });
            yield return AccessTools.Method(typeof(InputController), nameof(InputController.GetKeyDown), new[] { typeof(string) });
            yield return AccessTools.Method(typeof(InputController), nameof(InputController.GetKeyUp), new[] { typeof(string) });
        }

        static bool Prefix(string id_, ref bool __result)
        {
            var me = ScriptLoader.Instance;
            if (me == null || !me.DialogOpen || id_ == null) return true;
            InputController_keyDataSet k;
            if (InputController.keyDatas != null && InputController.keyDatas.TryGetValue(id_, out k) && Ours.Contains(k.keyCode)) { __result = false; return false; }
            return true;
        }
    }
}
