using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DotaGlass
{
    /// <summary>
    /// Controls the volume of the app that is playing music (its own Windows mixer level, e.g. just the
    /// browser), so you can balance music against Dota without touching the game or the PC volume.
    /// Never falls back to the master volume: if the app's audio can't be found, nothing changes.
    /// </summary>
    internal sealed class AppVolume
    {
        static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        const int CLSCTX_ALL = 0x17;
        const int DEVICE_STATE_ACTIVE = 1;
        static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(1);

        /// <summary>
        /// One audio session of the media app. Windows 11 25H2 no longer exposes ISimpleAudioVolume on
        /// other apps' sessions (E_NOINTERFACE), only IChannelAudioVolume, so use that when needed:
        /// the same level on every channel scales the app's sound just like its mixer slider.
        /// </summary>
        sealed class Target
        {
            public object Session;
            public ISimpleAudioVolume Simple;
            public IChannelAudioVolume Channels;
            public uint ChannelCount;
        }

        IMMDeviceEnumerator enumerator;
        readonly List<Target> targets = new List<Target>();
        string boundAumid = "";
        DateTime lastBindUtc = DateTime.MinValue;

        /// <summary>True when the media app's audio was found.</summary>
        public bool IsBound { get { return targets.Count > 0; } }

        /// <summary>Resolve which audio sessions belong to the media app (cheap; called on show / session change).</summary>
        public void Bind(string aumid)
        {
            boundAumid = aumid ?? "";
            lastBindUtc = DateTime.UtcNow;
            Release();
            if (boundAumid.Length == 0) return;
            try
            {
                if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                // every output device, not just the default one: the browser may play on headphones
                IMMDeviceCollection devices;
                if (enumerator.EnumAudioEndpoints(0 /* eRender */, DEVICE_STATE_ACTIVE, out devices) != 0 || devices == null) return;
                uint n;
                if (devices.GetCount(out n) == 0)
                {
                    for (uint d = 0; d < n; d++)
                    {
                        IMMDevice device;
                        if (devices.Item(d, out device) != 0 || device == null) continue;
                        AddSessions(device);
                        Marshal.ReleaseComObject(device);
                    }
                }
                Marshal.ReleaseComObject(devices);
            }
            catch { }
        }

        void AddSessions(IMMDevice device)
        {
            object o;
            Guid iid = IID_IAudioSessionManager2;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0 || o == null) return;
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator sessions;
            if (mgr.GetSessionEnumerator(out sessions) == 0 && sessions != null)
            {
                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    object so;
                    if (sessions.GetSession(i, out so) != 0 || so == null) continue;
                    Target t = null;
                    try { t = ToTarget(so); } catch { }
                    if (t != null) targets.Add(t);
                    else Marshal.ReleaseComObject(so);
                }
                Marshal.ReleaseComObject(sessions);
            }
            Marshal.ReleaseComObject(mgr);
        }

        Target ToTarget(object session)
        {
            var ctl = session as IAudioSessionControl2;
            uint pid;
            if (ctl == null || ctl.GetProcessId(out pid) != 0 || pid == 0) return null;
            string exe = Native.GetProcessFileName(pid);
            if (exe == null || !Matches(boundAumid, exe)) return null;

            var t = new Target { Session = session, Simple = session as ISimpleAudioVolume };
            if (t.Simple == null)
            {
                t.Channels = session as IChannelAudioVolume;
                if (t.Channels == null || t.Channels.GetChannelCount(out t.ChannelCount) != 0 || t.ChannelCount == 0) return null;
            }
            return t;
        }

        /// <summary>0..1, or 1 (the app's default level) if its audio isn't known yet.</summary>
        public double Get()
        {
            foreach (var t in targets)
            {
                try
                {
                    float v;
                    if (t.Simple != null)
                    {
                        if (t.Simple.GetMasterVolume(out v) == 0) return v;
                    }
                    else
                    {
                        var levels = new float[t.ChannelCount];
                        if (t.Channels.GetAllVolumes(t.ChannelCount, levels) == 0)
                        {
                            float max = 0;
                            foreach (float l in levels) if (l > max) max = l;
                            return max;
                        }
                    }
                }
                catch { }
            }
            return 1.0;
        }

        public void Set(double value)
        {
            float v = (float)Math.Max(0, Math.Min(1, value));
            if (targets.Count == 0)
            {
                // nothing found yet: the browser may have opened its audio stream since - look again
                if (DateTime.UtcNow - lastBindUtc < RetryEvery) return;
                Bind(boundAumid);
            }
            if (!Apply(v))
            {
                // session/device went away (e.g. headphones unplugged) - rebind once and retry
                Bind(boundAumid);
                Apply(v);
            }
        }

        bool Apply(float v)
        {
            bool ok = true;
            Guid ctx = Guid.Empty;
            foreach (var t in targets)
            {
                try
                {
                    int hr;
                    if (t.Simple != null) hr = t.Simple.SetMasterVolume(v, ref ctx);
                    else
                    {
                        var levels = new float[t.ChannelCount];
                        for (int i = 0; i < levels.Length; i++) levels[i] = v;
                        hr = t.Channels.SetAllVolumes(t.ChannelCount, levels, ref ctx);
                    }
                    if (hr != 0) ok = false;
                }
                catch { ok = false; }
            }
            return ok;
        }

        void Release()
        {
            foreach (var t in targets) try { Marshal.ReleaseComObject(t.Session); } catch { }
            targets.Clear();
        }

        /// <summary>Match a media AUMID (e.g. "Spotify.exe", "SpotifyAB.SpotifyMusic_..!Spotify", "Chrome", "MSEdge") to a process.</summary>
        static bool Matches(string aumid, string exe)
        {
            string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
            name = name.ToLowerInvariant();
            string a = aumid.ToLowerInvariant();
            if (name.Length >= 4 && a.Contains(name)) return true;
            if (name == "firefox" && MediaController.IsHex(aumid)) return true; // Firefox uses a hashed AUMID
            if (name == "browser" && a.Contains("yandex")) return true;          // Yandex Browser runs as browser.exe
            if (name == "microsoft.media.player" && a.Contains("zunemusic")) return true;
            return false;
        }

        // ---------------------------------------------------------------- Core Audio COM interop

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6F017C2EBE6E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
        }

        [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IChannelAudioVolume
        {
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolume(uint index, out float level);
            [PreserveSig] int SetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] levels, ref Guid ctx);
            [PreserveSig] int GetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0), Out] float[] levels);
        }
    }
}
