#if DEBUG
namespace Melvune.App.Services;

/// <summary>UI-thread batch regression. No keyboard injection or application preference writes.</summary>
internal static class GlobalHotkeyVerification
{
    internal static string Stage { get; private set; } = "not-started";

    internal static IReadOnlyDictionary<string, bool> Run(nint owner)
    {
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            Stage = name;
            checks.Add(name, value);
            if (!value) throw new InvalidOperationException($"Global hotkey regression failed: {name}");
        }

        Stage = "deterministic-registration";
        var native = new FakeNative { ConflictingKey = 0x25 };
        var commands = new List<DesktopShellCommand>();
        using (var service = new GlobalHotkeyService(1, commands.Add, native))
        {
            Check("hotkey-default-off", service.RegisteredCount == 0 && native.Attaches == 0);
            service.SetEnabled(true);
            Check("hotkey-partial-conflict", service.RegisteredCount == 4 && service.Conflicts.Count == 1
                && service.Conflicts[0].StartsWith("Ctrl+Alt+Left", StringComparison.Ordinal));
            Check("hotkey-native-modifiers", native.AllNoRepeat);
            service.SetEnabled(true);
            Check("hotkey-enable-idempotent", native.RegisterCalls == 5 && native.Attaches == 1);
            foreach (var binding in GlobalHotkeyService.Bindings) native.Send(binding.Id, binding.Key);
            Check("hotkey-command-route", commands.SequenceEqual(new[] { DesktopShellCommand.PlayPause,
                DesktopShellCommand.Next, DesktopShellCommand.ToggleDesktopLyrics, DesktopShellCommand.UnlockDesktopLyrics }));
            native.Send(0x5101, 0x55);
            native.Send(0x7FFF, 0x20);
            Check("hotkey-reject-unknown-or-wrong-key", commands.Count == 4);
            native.Send(0x5101, 0x20, 0);
            Check("hotkey-reject-wrong-modifiers", commands.Count == 4);
            service.SetEnabled(false);
            Check("hotkey-disable-cleans", service.RegisteredCount == 0 && native.Active.Count == 0
                && native.Detaches == 1 && service.Conflicts.Count == 0);
            native.Send(0x5101, 0x20);
            Check("hotkey-disabled-message-ignored", commands.Count == 4);
            service.SetEnabled(false);
            Check("hotkey-disable-idempotent", native.UnregisterCalls == 4 && native.Detaches == 1);
            native.ConflictingKey = 0;
            service.SetEnabled(true);
            Check("hotkey-retry-clears-conflict", service.RegisteredCount == 5 && service.Conflicts.Count == 0);
            native.Send(0x5102, 0x25);
            Check("hotkey-previous-route-after-retry", commands.Count == 5 && commands[^1] == DesktopShellCommand.Previous);
            service.Dispose();
            service.Dispose();
            service.SetEnabled(true);
            native.Send(0x5101, 0x20);
            Check("hotkey-dispose-cleans-and-stops", native.Active.Count == 0 && native.UnregisterCalls == 9
                && native.Detaches == 2 && commands.Count == 5);
        }

        var failNative = new FakeNative { AllowAttach = false };
        using (var service = new GlobalHotkeyService(1, _ => { }, failNative))
        {
            service.SetEnabled(true);
            Check("hotkey-attach-failure-no-registration", service.RegisteredCount == 0 && service.Conflicts.Count == 1
                && failNative.RegisterCalls == 0);
            failNative.AllowAttach = true;
            service.SetEnabled(true);
            Check("hotkey-attach-failure-retry", service.RegisteredCount == 5 && service.Conflicts.Count == 0);
        }

        var throwNative = new FakeNative();
        int attempted = 0;
        using (var service = new GlobalHotkeyService(1, _ => { attempted++; throw new InvalidOperationException("fixture"); }, throwNative))
        {
            service.SetEnabled(true);
            throwNative.Send(0x5101, 0x20);
            throwNative.Send(0x5101, 0x20);
            Check("hotkey-callback-exception-contained", attempted == 2 && service.RegisteredCount == 5);
            throwNative.SendDestroy();
            Check("hotkey-window-destruction-cleans", service.RegisteredCount == 0 && throwNative.Active.Count == 0
                && throwNative.Detaches == 1);
        }

        // Exercise the real Windows registration and cleanup. Existing external conflicts are
        // accepted, and deterministic tests above cover the partial conflict policy independently.
        Stage = "native-registration";
        using (var service = new GlobalHotkeyService(owner, _ => { }))
        {
            Check("hotkey-real-default-off", service.RegisteredCount == 0);
            service.SetEnabled(true);
            Check("hotkey-real-registration-accounted", service.RegisteredCount + service.Conflicts.Count == 5);
            var acquired = GlobalHotkeyService.Bindings.Where(b => !service.Conflicts.Any(c =>
                c.StartsWith(b.Label, StringComparison.Ordinal))).ToArray();
            int count = service.RegisteredCount;
            service.SetEnabled(true);
            Check("hotkey-real-enable-idempotent", service.RegisteredCount == count);
            service.SetEnabled(false);
            Check("hotkey-real-disable-cleans", service.RegisteredCount == 0 && service.Conflicts.Count == 0);
            Check("hotkey-real-disable-releases-combinations", CanClaimReleased(acquired));
            service.SetEnabled(true);
            acquired = GlobalHotkeyService.Bindings.Where(b => !service.Conflicts.Any(c =>
                c.StartsWith(b.Label, StringComparison.Ordinal))).ToArray();
            service.Dispose();
            Check("hotkey-real-dispose-releases-combinations", service.RegisteredCount == 0 && CanClaimReleased(acquired));
        }
        Stage = "complete";
        return checks;
    }

    private static bool CanClaimReleased(IEnumerable<GlobalHotkeyService.Binding> bindings)
    {
        // Thread-bound test identifier, unrelated to all service/window identifiers.
        const int probeId = 0x5191;
        foreach (var binding in bindings)
        {
            if (!GlobalHotkeyService.RegisterHotKey(0, probeId, GlobalHotkeyService.Modifiers, binding.Key)) return false;
            try { }
            finally { GlobalHotkeyService.UnregisterHotKey(0, probeId); }
        }
        return true;
    }

    private sealed class FakeNative : GlobalHotkeyService.IHotkeyNative
    {
        internal uint ConflictingKey;
        internal bool AllowAttach = true;
        internal int Attaches, Detaches, RegisterCalls, UnregisterCalls;
        internal bool AllNoRepeat = true;
        internal HashSet<int> Active { get; } = [];
        private GlobalHotkeyService.SubclassProcedure? procedure;
        public bool IsOwnerThread(nint owner) => true;
        public bool Attach(nint owner, GlobalHotkeyService.SubclassProcedure callback, nuint id)
        {
            Attaches++;
            if (!AllowAttach) return false;
            procedure = callback;
            return id == GlobalHotkeyService.SubclassId;
        }
        public void Detach(nint owner, GlobalHotkeyService.SubclassProcedure callback, nuint id) => Detaches++;
        public bool Register(nint owner, int id, uint modifiers, uint key)
        {
            RegisterCalls++;
            AllNoRepeat &= modifiers == GlobalHotkeyService.Modifiers;
            return key != ConflictingKey && Active.Add(id);
        }
        public bool Unregister(nint owner, int id) { UnregisterCalls++; return Active.Remove(id); }
        public nint Forward(nint owner, uint message, nuint wParam, nint lParam) => 9;
        internal void Send(int id, uint key, uint modifiers = 3) => procedure?.Invoke(1,
            GlobalHotkeyService.HotkeyMessage, (nuint)id, (nint)((key << 16) | modifiers), GlobalHotkeyService.SubclassId, 0);
        internal void SendDestroy() => procedure?.Invoke(1, GlobalHotkeyService.DestroyMessage, 0, 0, GlobalHotkeyService.SubclassId, 0);
    }
}
#endif
